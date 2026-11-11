using System.Formats.Tar;
using ICSharpCode.SharpZipLib.BZip2;
using MeetingAssistant.Core;

namespace MeetingAssistant.Speech;

/// One downloadable model: a folder under `AppPaths.Models` and where its files come from.
/// `Archive` true = a .tar.bz2 whose listed files are taken from the archive's top folder.
public sealed record ModelSpec(string Folder, string Title, string Url, bool Archive, IReadOnlyDictionary<string, string> Files)
{
    /// Folder of this model (complete only when it exists: downloads go to a temp folder first).
    public string Directory => Path.Combine(AppPaths.Models, Folder);
    public string File(string name) => Path.Combine(Directory, name);
    public bool IsReady => Files.Keys.All(f => System.IO.File.Exists(File(f)));
}

/// The sherpa-onnx models the Windows app uses (spec 01-recording, "Model download"), all from
/// the k2-fsa/sherpa-onnx GitHub releases.
public static class ModelCatalog
{
    const string Releases = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";

    /// Silero VAD (single ONNX file, ~0.6 MB).
    public static readonly ModelSpec SileroVad = new("silero-vad", "Voice activity detection",
        Releases + "asr-models/silero_vad.onnx", false,
        new Dictionary<string, string> { ["silero_vad.onnx"] = "silero_vad.onnx" });

    /// pyannote segmentation 3.0 (offline diarization, ~7 MB archive).
    public static readonly ModelSpec Segmentation = new("pyannote-segmentation-3-0", "Speaker diarization (pyannote segmentation 3.0)",
        Releases + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2", true,
        new Dictionary<string, string> { ["model.onnx"] = "model.onnx" });

    /// 3D-Speaker CAM++ speaker embeddings (192-d, ~28 MB).
    public static readonly ModelSpec CampPlus = new("campplus", "Speaker embeddings (CAM++)",
        Releases + "speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx", false,
        new Dictionary<string, string> { ["campplus.onnx"] = "campplus.onnx" });

    /// NVIDIA Parakeet TDT 0.6B "ultra" (the model the Mac's FluidAudio calls Parakeet Ultra),
    /// int8 (~490 MB archive). On quiet room recordings it found ~25 % more words than v3.
    public static readonly ModelSpec Parakeet = new("parakeet-ultra", "Parakeet Ultra (live transcription)",
        Releases + "asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-ultra-int8.tar.bz2", true,
        new Dictionary<string, string>
        {
            ["encoder.int8.onnx"] = "encoder.int8.onnx",
            ["decoder.int8.onnx"] = "decoder.int8.onnx",
            ["joiner.int8.onnx"] = "joiner.int8.onnx",
            ["tokens.txt"] = "tokens.txt",
        });

    /// Download order with the spec's progress fractions (start of each step).
    public static readonly IReadOnlyList<(ModelSpec Model, double From, double To)> Steps = new[]
    {
        (SileroVad, 0.02, 0.08),
        (Segmentation, 0.08, 0.2),
        (CampPlus, 0.2, 0.3),
        (Parakeet, 0.3, 1.0),
    };

    /// "Models ready": the four core model folders are complete.
    public static bool CoreModelsReady() => Steps.All(s => s.Model.IsReady);

    static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// Download every missing model with progress (fraction, message); ends with (1, "Ready").
    /// A model whose files exist is skipped. Throws on failure (the UI shows "Model download failed: …").
    public static async Task DownloadAllAsync(Action<double, string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.Models);
        foreach (var (model, from, to) in Steps)
        {
            progress?.Invoke(from, model.Title);
            if (model.IsReady) continue;
            await DownloadAsync(model, f => progress?.Invoke(from + (to - from) * f, model.Title), ct);
        }
        progress?.Invoke(1, "Ready");
    }

    /// Download and install one model into its folder (temp folder first, then renamed).
    public static async Task DownloadAsync(ModelSpec model, Action<double>? progress, CancellationToken ct)
    {
        var tmpDir = model.Directory + ".download";
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        Directory.CreateDirectory(tmpDir);
        var payload = Path.Combine(tmpDir, model.Archive ? "archive.tar.bz2" : model.Files.Values.First());
        try
        {
            using (var resp = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(payload);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                double lastReport = -1;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total is > 0)
                    {
                        double f = 0.95 * done / total.Value;
                        if (f - lastReport >= 0.005) { lastReport = f; progress?.Invoke(f); }
                    }
                }
            }
            if (model.Archive)
            {
                ExtractTarBz2(payload, tmpDir, model.Files);
                File.Delete(payload);
            }
            else if (Path.GetFileName(payload) != model.Files.Keys.First())
            {
                File.Move(payload, Path.Combine(tmpDir, model.Files.Keys.First()), true);
            }
            foreach (var f in model.Files.Keys)
                if (!File.Exists(Path.Combine(tmpDir, f))) throw new IOException($"{model.Folder}: {f} missing in the download");
            if (Directory.Exists(model.Directory)) Directory.Delete(model.Directory, true);
            Directory.Move(tmpDir, model.Directory);
            progress?.Invoke(1);
        }
        catch
        {
            try { Directory.Delete(tmpDir, true); } catch { }
            throw;
        }
    }

    /// Extract the wanted files (archive file name → local name; the archive's top folder is ignored).
    static void ExtractTarBz2(string archive, string targetDir, IReadOnlyDictionary<string, string> files)
    {
        var wanted = files.ToDictionary(kv => kv.Value, kv => kv.Key);
        using var fs = File.OpenRead(archive);
        using var bz = new BZip2InputStream(fs);
        using var tar = new TarReader(bz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var name = Path.GetFileName(entry.Name);
            if (!wanted.TryGetValue(name, out var local)) continue;
            entry.ExtractToFile(Path.Combine(targetDir, local), true);
        }
    }
}
