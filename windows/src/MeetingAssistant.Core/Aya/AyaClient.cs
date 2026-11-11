using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Aya;

/// <summary>
/// Client for the Aya REST API (spec 02 §8.1–8.3). Auth: <c>Authorization: Bearer &lt;org API key&gt;</c>.
/// An org key belongs to one organization, whose id must be configured next to it.
/// </summary>
public sealed class AyaClient
{
    /// <summary>The API base URL.</summary>
    public const string DefaultBase = "https://api.hey-aya.ai";

    public string OrgId { get; }
    readonly string _apiKey;
    readonly Uri _base;
    readonly HttpClient _http;
    readonly Func<TimeSpan, Task> _delay;

    /// <param name="handler">For tests: a fake transport. Default: a handler without cookies or cache.</param>
    /// <param name="delay">For tests: replaces the retry sleeps.</param>
    public AyaClient(string orgId, string apiKey, string baseUrl = DefaultBase, HttpMessageHandler? handler = null,
                     Func<TimeSpan, Task>? delay = null)
    {
        OrgId = orgId.Trim();
        _apiKey = apiKey.Trim();
        _base = new Uri(baseUrl.TrimEnd('/'));
        handler ??= new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _delay = delay ?? (t => Task.Delay(t));
    }

    // MARK: Reads

    /// <summary>Cheap key check. Returns the key owner's user id.</summary>
    public async Task<string> MeAsync() => (await GetNodeAsync("/api/auth/me"))?["userId"].AsString() ?? "";

    public Task<List<AyaWorkspace>> WorkspacesAsync() =>
        GetAsync<List<AyaWorkspace>>("/api/workspaces", new() { ["orgId"] = OrgId });

    public Task<List<AyaRecordSpec>> RecordSpecsAsync(string workspace) =>
        GetAsync<List<AyaRecordSpec>>($"/api/workspaces/{workspace}/record-specs");

    public Task<List<AyaRecord>> RecordsAsync(string workspace, string list, AyaQuery? query = null) =>
        GetAsync<List<AyaRecord>>($"/api/workspaces/{workspace}/records/{list}",
                                  new() { ["query"] = JsonIO.Compact((query ?? new AyaQuery()).Json()) });

    /// <summary>All records, paging 200 at a time until a short page or <paramref name="max"/> (spec 02 §8.2).</summary>
    public async Task<List<AyaRecord>> AllRecordsAsync(string workspace, string list, string? sortField = null, int max = 2000)
    {
        var output = new List<AyaRecord>();
        var q = new AyaQuery { SortField = sortField, Limit = 200 };
        while (output.Count < max)
        {
            var page = await RecordsAsync(workspace, list, q);
            output.AddRange(page);
            if (page.Count < q.Limit) break;
            q.Offset += q.Limit;
        }
        return output;
    }

    public Task<List<AyaRecord>> SearchAsync(string workspace, string list, string text, int limit = 20) =>
        GetAsync<List<AyaRecord>>($"/api/workspaces/{workspace}/records/{list}/search",
                                  new() { ["q"] = text, ["limit"] = limit.ToString() });

    public Task<AyaRecord> RecordAsync(string workspace, string list, string id) =>
        GetAsync<AyaRecord>($"/api/workspaces/{workspace}/records/{list}/{id}");

    /// <summary>The workspace's OpenAPI document, fetched without the auth header (spec 02 §8.2).</summary>
    public Task<byte[]> OpenApiAsync(string workspace) => SendAsync(HttpMethod.Get, $"/api/workspaces/{workspace}/openapi.json", auth: false);

    // MARK: Writes

    /// <summary>Creates a record. Values use record keys (<c>title__value</c>, <c>firma__id</c>, …); a null value writes JSON null.</summary>
    public async Task<AyaRecord> CreateAsync(string workspace, string list, IReadOnlyDictionary<string, JsonNode?> values) =>
        Decode<AyaRecord>(await SendAsync(HttpMethod.Post, $"/api/workspaces/{workspace}/records/{list}", body: Body(values)));

    /// <summary>Partial update: only the given keys change.</summary>
    public async Task<AyaRecord> UpdateAsync(string workspace, string list, string id, IReadOnlyDictionary<string, JsonNode?> values) =>
        Decode<AyaRecord>(await SendAsync(HttpMethod.Put, $"/api/workspaces/{workspace}/records/{list}/{id}", body: Body(values)));

    public Task DeleteAsync(string workspace, string list, string id) =>
        SendAsync(HttpMethod.Delete, $"/api/workspaces/{workspace}/records/{list}/{id}");

    /// <summary>The request body <c>{"values":{…}}</c>.</summary>
    public static string Body(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var v = new JsonObject();
        foreach (var (k, x) in values) v[k] = x?.DeepClone();
        return JsonIO.Compact(new JsonObject { ["values"] = v });
    }

    // MARK: Transport

    async Task<T> GetAsync<T>(string path, Dictionary<string, string>? query = null) =>
        Decode<T>(await SendAsync(HttpMethod.Get, path, query));

    async Task<JsonNode?> GetNodeAsync(string path)
    {
        var data = await SendAsync(HttpMethod.Get, path);
        try { return JsonNode.Parse(data); }
        catch (JsonException e) { throw new AyaException(AyaErrorKind.Decoding, Clip(e.Message, 300)); }
    }

    static T Decode<T>(byte[] data)
    {
        try { return JsonSerializer.Deserialize<T>(data, JsonIO.Options) ?? throw new JsonException("empty answer"); }
        catch (JsonException e) { throw new AyaException(AyaErrorKind.Decoding, Clip(e.Message, 300)); }
    }

    static string Clip(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>The full request URL; query values are percent-encoded including <c>+</c> (spec 02 §8.1).</summary>
    public Uri Url(string path, IReadOnlyDictionary<string, string>? query = null)
    {
        var s = _base.AbsoluteUri.TrimEnd('/') + path;
        if (query is { Count: > 0 })
            s += "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        return new Uri(s);
    }

    async Task<byte[]> SendAsync(HttpMethod method, string path, Dictionary<string, string>? query = null, string? body = null, bool auth = true)
    {
        if (OrgId.Length == 0 || _apiKey.Length == 0) throw new AyaException(AyaErrorKind.NotConfigured);
        var url = Url(path, query);
        int attempt = 0;
        while (true)
        {
            attempt++;
            using var req = new HttpRequestMessage(method, url);
            if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null) req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");

            HttpResponseMessage resp;
            byte[] data;
            try
            {
                resp = await _http.SendAsync(req);
                data = await resp.Content.ReadAsByteArrayAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt < 3) { await _delay(TimeSpan.FromSeconds(attempt)); continue; }
                throw new AyaException(AyaErrorKind.Network, e.Message);
            }
            using (resp)
            {
                var code = (int)resp.StatusCode;
                if (code is >= 200 and < 300) return data;
                JsonNode? err = null;
                try { err = JsonNode.Parse(data); } catch { }
                var errCode = err is JsonObject ? err["code"].AsString() ?? "" : "";
                var bodyText = Encoding.UTF8.GetString(data, 0, Math.Min(300, data.Length));
                var message = (err is JsonObject ? err["error"].AsString() : null) ?? bodyText;
                switch (code)
                {
                    case 402: throw new AyaException(AyaErrorKind.CreditsExhausted);
                    case 429:
                        if (errCode == "limit_exceeded") throw new AyaException(AyaErrorKind.CreditsExhausted);
                        if (attempt < 4)
                        {
                            double wait = attempt * 2;
                            if (resp.Headers.TryGetValues("Retry-After", out var ra)
                                && double.TryParse(ra.FirstOrDefault(), System.Globalization.NumberStyles.Float,
                                                   System.Globalization.CultureInfo.InvariantCulture, out var w)) wait = w;
                            await _delay(TimeSpan.FromSeconds(Math.Min(wait, 30)));
                            continue;
                        }
                        throw new AyaException(AyaErrorKind.Server, message, 429);
                    case 502 or 503 or 504:
                        if (attempt < 4) { await _delay(TimeSpan.FromSeconds(attempt)); continue; }
                        throw new AyaException(AyaErrorKind.Server, message, code);
                    case 401: throw new AyaException(AyaErrorKind.Unauthorized);
                    case 403: throw new AyaException(AyaErrorKind.Forbidden, message, code);
                    case 404: throw new AyaException(AyaErrorKind.NotFound, message, code);
                    case 400 or 422: throw new AyaException(AyaErrorKind.BadRequest, message.Length == 0 ? bodyText : message, code);
                    default: throw new AyaException(AyaErrorKind.Server, message, code);
                }
            }
        }
    }
}
