# /// script
# dependencies = ["numpy", "soundfile", "scipy"]
# ///
import json, subprocess, tempfile, os, numpy as np, soundfile as sf
from scipy.signal import fftconvolve
SR = 16000
ME = "Flo (German (Germany))"
script = [
 (ME, "Guten Morgen zusammen. Lasst uns mit dem Status zum Kundenprojekt anfangen."),
 ("Anna", "Gerne. Wir haben letzte Woche die Schnittstelle zum Lagerverwaltungssystem fertiggestellt und die ersten Tests laufen."),
 ("Eddy (German (Germany))", "Bei den Tests sind uns allerdings zwei Fehler in der Datumsumrechnung aufgefallen. Die beheben wir bis Mittwoch."),
 (ME, "Gut. Gibt es Risiken für den Liefertermin Ende November?"),
 ("Reed (German (Germany))", "Aus meiner Sicht nicht. Das Budget liegt bei etwa achtzig Prozent, wir sind also im Plan."),
 ("Daniel", "Quick note from the London team: we need the API documentation in English before the review on Friday."),
 ("Anna", "Das übernehme ich. Ich schicke die Dokumentation bis Donnerstag an alle."),
 (ME, "Perfekt. Dann halten wir fest: Eddy behebt die Fehler, Anna kümmert sich um die Doku, und wir sehen uns nächste Woche wieder."),
 ("Eddy (German (Germany))", "Alles klar, bis dann."),
]
def tts(voice, text):
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "x.wav")
        subprocess.run(["say", "-v", voice, "-o", p, "--data-format=LEI16@16000", text], check=True)
        a, sr = sf.read(p, dtype="float32"); assert sr == SR
        return a
gap = int(0.6*SR)
mic, sysa, truth, t = [], [], [], 0
for voice, text in script:
    a = tts(voice, text)
    is_me = voice == ME
    z = np.zeros_like(a)
    mic.append(a if is_me else z); sysa.append(z if is_me else a)
    truth.append({"speaker": voice.split(" ")[0], "start": t/SR, "end": (t+len(a))/SR, "text": text, "track": "mic" if is_me else "system"})
    t += len(a) + gap
    mic.append(np.zeros(gap, np.float32)); sysa.append(np.zeros(gap, np.float32))
mic = np.concatenate(mic); sysa = np.concatenate(sysa)
rng = np.random.default_rng(0)
# call scenario: light noise on mic
sf.write("call_mic.wav", mic*0.8 + rng.normal(0, 0.002, len(mic)).astype(np.float32), SR)
sf.write("call_system.wav", sysa*0.8, SR)
# in-person scenario: one mic, everyone at different distance, synthetic reverb, room noise
def rir(rt60, seed):
    r = np.random.default_rng(seed); n = int(rt60*SR)
    h = r.normal(0,1,n)*np.exp(-6.9*np.arange(n)/n); h[0] = 3.0; return (h/np.abs(h).sum()*8).astype(np.float32)
room = np.zeros(len(mic), np.float32); pos = 0
for i,(voice,text) in enumerate(script):
    seg = truth[i]; s0, s1 = int(seg["start"]*SR), int(seg["end"]*SR)
    src = (mic if seg["track"]=="mic" else sysa)[s0:s1]
    gain = 1.0 if seg["track"]=="mic" else 0.45
    wet = fftconvolve(src, rir(0.45, hash(voice)%1000))[: len(src)+int(0.45*SR)] * gain
    room[s0:s0+len(wet)] += wet[: len(room)-s0]
room += rng.normal(0, 0.006, len(room)).astype(np.float32)
room *= 0.9/np.abs(room).max()
sf.write("room_mic.wav", room, SR)
json.dump(truth, open("truth.json","w"), indent=1, ensure_ascii=False)
print("duration %.1fs" % (len(mic)/SR))
