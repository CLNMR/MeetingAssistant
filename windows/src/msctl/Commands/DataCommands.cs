using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace Msctl.Commands;

/// Data-file checks.
public sealed class DataCommands : ICommandGroup
{
    public IEnumerable<Command> Commands => new[]
    {
        new Command("json-roundtrip", "json-roundtrip <session.json|transcript.json|...> — read with the model types and print as written",
            args =>
            {
                if (args.Length != 1) throw new UsageException();
                var path = args[0];
                var name = Path.GetFileName(path);
                string text = name switch
                {
                    "session.json" => JsonIO.Serialize(JsonIO.Read<SessionInfo>(path)),
                    "transcript.json" => JsonIO.Serialize(JsonIO.Read<Transcript>(path)),
                    "me-voiceprint.json" => JsonIO.Serialize(JsonIO.Read<Voiceprint>(path)),
                    "speaker-library.json" => JsonIO.Serialize(JsonIO.Read<SpeakerLibrary>(path)),
                    _ => JsonIO.Format(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))),
                };
                Console.WriteLine(text);
                return Task.FromResult(0);
            }),
    };
}

public sealed class AudioCommands : ICommandGroup
{
    public IEnumerable<Command> Commands => new[]
    {
        new Command("audio-info", "audio-info <file.caf|file.wav> — sample count and duration as read by the app",
            args =>
            {
                if (args.Length != 1) throw new UsageException();
                var s = MeetingAssistant.Core.Audio.AudioFile.Read(args[0]);
                double rms = Math.Sqrt(s.Select(x => (double)x * x).DefaultIfEmpty(0).Average());
                Console.WriteLine($"samples={s.Length} seconds={s.Length / 16000.0:F2} rms={rms:F4}");
                return Task.FromResult(0);
            }),
    };
}
