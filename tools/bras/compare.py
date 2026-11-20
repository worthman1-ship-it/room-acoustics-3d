# Compares the app's CR2 results (sim_*.csv, octave bands) with the BRAS measurements (third octaves 80 Hz - 16 kHz, mean over 2 sources x 5 receivers).
# An octave's measured value is the mean of its three third octaves. Just-noticeable differences (ISO 3382-1 Annex A): T and EDT 5 %, C80 1 dB, D50 5 %.
import sys, csv, os
# usage: python compare.py <BRAS root> sim_*.csv ...
add = os.path.join(sys.argv[1], 'additional')
octaves = [125, 250, 500, 1000, 2000, 4000, 8000]
thirds = {125: (100, 125, 155), 250: (200, 250, 315), 500: (400, 500, 630), 1000: (800, 1000, 1250), 2000: (1600, 2000, 2500), 4000: (3150, 4000, 5000), 8000: (6350, 8000, 10000)}
def measured(name):
    rows = open(os.path.join(add, f'Evaluation_CR2_{name}.txt')).read().split()
    f = [float(x) for x in rows[0].split(',')]; v = [float(x) for x in rows[1].split(',')]
    m = dict(zip(f, v))
    return {o: sum(m[t] for t in thirds[o]) / 3 for o in octaves}
meas = {k: measured(k) for k in ('T20', 'EDT', 'C80', 'D50')}
for path in sys.argv[2:]:
    sim = {int(r['band']): r for r in csv.DictReader(open(path))}
    print(f'\n{os.path.basename(path)}')
    print(f"{'band':>6} | {'T20 meas':>8} {'sim':>6} {'diff':>6} | {'EDT meas':>8} {'sim':>6} {'diff':>6} | {'C80 meas':>8} {'sim':>6} {'dB':>5} | {'D50 meas':>8} {'sim':>6} {'pts':>5}")
    jnd = {'T20': 0, 'EDT': 0, 'C80': 0, 'D50': 0}
    for o in octaves:
        s = sim[o]
        t, e, c, d = float(s['T20']), float(s['EDT']), float(s['C80']), float(s['D50'])
        dt = (t / meas['T20'][o] - 1) * 100; de = (e / meas['EDT'][o] - 1) * 100
        dc = c - meas['C80'][o]; dd = d - meas['D50'][o]
        jnd['T20'] += abs(dt) / 5; jnd['EDT'] += abs(de) / 5; jnd['C80'] += abs(dc) / 1; jnd['D50'] += abs(dd) / 5
        print(f"{o:>6} | {meas['T20'][o]:8.2f} {t:6.2f} {dt:+5.0f}% | {meas['EDT'][o]:8.2f} {e:6.2f} {de:+5.0f}% | {meas['C80'][o]:8.1f} {c:6.1f} {dc:+5.1f} | {meas['D50'][o]:8.1f} {d:6.1f} {dd:+5.1f}")
    print('mean error in JNDs: ' + ', '.join(f'{k} {v / len(octaves):.1f}' for k, v in jnd.items()))
