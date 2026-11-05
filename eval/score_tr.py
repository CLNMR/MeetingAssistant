# /// script
# dependencies = ["jiwer"]
# ///
import json, sys, re, jiwer
truth = json.load(open(sys.argv[1]))
def norm(s):
    s = s.lower().replace("80 prozent","achtzig prozent").replace("80%","achtzig prozent")
    return " ".join(re.sub(r"[^\wäöüß ]+"," ",s).split())
ref = norm(" ".join(t["text"] for t in truth))
for p in sys.argv[2:]:
    tr = json.load(open(p)); labels = {s["id"]: s["label"] for s in tr["speakers"]}
    hyp = norm(" ".join(t["text"] for t in tr["turns"]))
    # speaker attribution: for each truth utterance, majority label of words inside it
    correct = {}; 
    for u in truth:
        ws = [(w, labels[t["speaker"]]) for t in tr["turns"] for w in t["words"] if u["start"]-0.3 <= (w["start"]+w["end"])/2 <= u["end"]+0.3]
        if ws:
            top = max(set(l for _,l in ws), key=[l for _,l in ws].count)
            correct.setdefault(u["speaker"], []).append(top)
    print(f"WER {jiwer.wer(ref,hyp)*100:5.1f}%  {p.split('/')[-1]}  speakers: " + ", ".join(f"{k}->{'/'.join(sorted(set(v)))}" for k,v in correct.items()))
