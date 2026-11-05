# /// script
# dependencies = ["numpy", "soundfile", "pyroomacoustics"]
# ///
import json, numpy as np, soundfile as sf, pyroomacoustics as pra
SR = 16000
truth = json.load(open("truth.json"))
mic, _ = sf.read("call_mic.wav", dtype="float32"); sysa, _ = sf.read("call_system.wav", dtype="float32")
# 6x5x3 m meeting room, RT60 0.5 s, laptop on the table, people 0.6 - 2.5 m away
rt60, dims = 0.5, [6, 5, 3]
e_abs, max_order = pra.inverse_sabine(rt60, dims)
positions = {"Flo": [3.0, 1.9, 1.2], "Anna": [1.6, 3.2, 1.2], "Eddy": [4.6, 3.4, 1.3], "Reed": [3.0, 4.3, 1.2], "Daniel": [1.2, 1.5, 1.3]}
room = pra.ShoeBox(dims, fs=SR, materials=pra.Material(e_abs), max_order=max_order)
for t in truth:
    s0, s1 = int(t["start"]*SR), int(t["end"]*SR)
    src = (mic if t["track"] == "mic" else sysa)[s0:s1]
    room.add_source(positions[t["speaker"]], signal=src, delay=t["start"])
room.add_microphone([3.0, 2.5, 0.8])
room.simulate()
out = room.mic_array.signals[0][: len(mic)]
rng = np.random.default_rng(1)
noise = rng.normal(0, 1, len(out)); noise *= np.sqrt(np.mean(out**2) / 10**(25/10)) / np.std(noise)  # 25 dB SNR
out = out + noise; out = 0.9 * out / np.abs(out).max()
sf.write("room_mic.wav", out.astype(np.float32), SR)
print("ok")
