# /// script
# dependencies = ["jiwer"]
# ///
import sys, re, jiwer
ref = {}
for l in open(sys.argv[1]):
    f = l.rstrip("\n").split("\t"); ref[f[1]] = f[3]
def norm(s): return " ".join(re.sub(r"[^\wäöüß ]+", " ", s.lower()).split())
for p in sys.argv[2:]:
    R, H = [], []
    for l in open(p):
        name, _, text = l.rstrip("\n").partition("\t")
        if name in ref: R.append(norm(ref[name])); H.append(norm(text) or "<empty>")
    print(f"{p:28s} n={len(R)}  WER {jiwer.wer(R, H)*100:5.2f}%")
