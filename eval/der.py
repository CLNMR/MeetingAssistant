# /// script
# dependencies = ["pyannote.metrics", "pyannote.core"]
# ///
import json, sys
from pyannote.core import Annotation, Segment
from pyannote.metrics.diarization import DiarizationErrorRate
def rttm(p):
    a = Annotation()
    for l in open(p):
        f = l.split(); a[Segment(float(f[3]), float(f[3]) + float(f[4]))] = f[7]
    return a
def c1(p):
    a = Annotation()
    for s in json.load(open(p))["segments"]:
        a[Segment(s["startTimeSeconds"], s["endTimeSeconds"])] = s["speakerId"]
    return a
ref = rttm(sys.argv[1]); m = DiarizationErrorRate(collar=0.25, skip_overlap=True)
for p in sys.argv[2:]:
    hyp = c1(p) if p.endswith(".json") else rttm(p)
    print(f"DER {m(ref, hyp)*100:5.1f}%  speakers found {len(hyp.labels())} (true {len(ref.labels())})  {p}")
