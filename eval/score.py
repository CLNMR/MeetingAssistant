# /// script
# dependencies = ["jiwer"]
# ///
import json, re, sys, jiwer
truth = json.load(open(sys.argv[1]))
which = sys.argv[2]  # all | system | mic
ref = " ".join(t["text"] for t in truth if which == "all" or t["track"] == which)
def norm(s):
    s = s.lower().replace("80%", "achtzig prozent").replace("80 %", "achtzig prozent")
    s = re.sub(r"[^\wäöüß ]+", " ", s)
    return " ".join(s.split())
for path in sys.argv[3:]:
    try:
        d = json.load(open(path)); hyp = d.get("text") or " ".join(x.get("text","") for x in d.get("segments", []))
    except Exception:
        hyp = open(path).read()
    print(f"{jiwer.wer(norm(ref), norm(hyp))*100:6.1f}%  {path}")
