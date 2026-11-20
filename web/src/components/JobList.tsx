import { useRef, useState } from 'react';
import { api, type SimulateParams } from '../api';
import { ISO_ROWS } from '../iso';
import { download } from '../project';
import type { SimulateResponse } from '../types';
import { Note, Section } from './ui';

/** One calculation in the job list: what Run simulation would have sent when it was added, and how it went. */
export type Job = {
  id: number; name: string; params: SimulateParams; what: string;
  status: 'waiting' | 'running' | 'done' | 'failed'; result?: SimulateResponse; error?: string; progress?: number;
};

/** The job list: setups added one by one and calculated one after another, in the order they were added. */
export function useJobs() {
  const [jobs, setJobs] = useState<Job[]>([]);
  const next = useRef(1);
  const [running, setRunning] = useState(false);
  const abort = useRef<AbortController | null>(null);
  const change = (id: number, patch: Partial<Job>) => setJobs((list) => list.map((j) => (j.id === id ? { ...j, ...patch } : j)));

  const add = (params: SimulateParams, what: string, name?: string) => {
    const id = next.current++;
    setJobs((list) => [...list, { id, name: name?.trim() || `Job ${id}`, params: structuredClone(params), what, status: 'waiting' }]);
  };
  const remove = (id: number) => setJobs((list) => list.filter((j) => j.id !== id || j.status === 'running'));
  const rename = (id: number, name: string) => change(id, { name });

  /** Calculates every job not yet done, one at a time; Cancel stops the one running and leaves the rest waiting. */
  const runAll = async (waiting: Job[]) => {
    const controller = new AbortController();
    abort.current = controller;
    setRunning(true);
    try {
      for (const job of waiting) {
        if (controller.signal.aborted) break;
        change(job.id, { status: 'running', progress: 0, error: undefined });
        try {
          const result = await api.simulate(job.params, controller.signal, (progress) => change(job.id, { progress }));
          change(job.id, { status: 'done', result });
        } catch (e) {
          const cancelled = (e as Error).name === 'AbortError';
          change(job.id, cancelled ? { status: 'waiting' } : { status: 'failed', error: (e as Error).message });
          if (cancelled) break;
        }
      }
    } finally {
      setRunning(false);
    }
  };
  return { jobs, add, remove, rename, running, runAll: () => runAll(jobs.filter((j) => j.status === 'waiting' || j.status === 'failed')), cancel: () => abort.current?.abort() };
}

const cell = (v: number | null | undefined, digits: number) => (v === null || v === undefined ? '–' : v.toFixed(digits));

/** Every receiver of every job done, its ISO 3382 single numbers and STI, as CSV rows. */
export function jobsCsv(jobs: Job[]): string {
  const quote = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
  const rows = [['Job', 'Setup', 'Receiver', 'x (m)', 'y (m)', 'z (m)', 'Parameter', 'Unit', 'Value']];
  for (const job of jobs) {
    if (!job.result) continue;
    const receivers = job.result.receivers ?? [];
    receivers.forEach((r, i) => {
      const where = [r.position.x, r.position.y, r.position.z].map((v) => v.toFixed(2));
      for (const row of ISO_ROWS) rows.push([job.name, job.what, String(i + 1), ...where, row.label, row.unit, cell(r.broadband[row.key], 4)]);
      rows.push([job.name, job.what, String(i + 1), ...where, 'STI', '', cell(r.sti.sti, 3)]);
    });
  }
  return rows.map((r) => r.map(quote).join(',')).join('\r\n') + '\r\n';
}

type Props = ReturnType<typeof useJobs> & {
  /** What Run simulation would send now, and a few words saying what it is; null when it cannot be run. */
  current: SimulateParams | null; what: string;
};

/** The job list's window: add the setup as it is, run them all, and compare what they give at the main receiver. */
export default function JobList({ jobs, add, remove, rename, running, runAll, cancel, current, what }: Props) {
  const [name, setName] = useState('');
  const done = jobs.filter((j) => j.result);
  const toRun = jobs.filter((j) => j.status === 'waiting' || j.status === 'failed').length;
  return (
    <>
      <Section title="Job list">
        <Note>Set up the room, finishes, sources and receivers, then add them as a job; change something and add another. Run all calculates them one after another with the calculation settings each was added with, and compares them below. The list lasts while the page is open; it is not saved with the project.</Note>
        <div className="flex gap-1.5">
          <input aria-label="Job name" className="field flex-1" placeholder={`Job ${jobs.length + 1}`} value={name} onChange={(e) => setName(e.target.value)} />
          <button type="button" className="chip" disabled={!current} onClick={() => { if (current) { add(current, what, name); setName(''); } }}>Add current setup</button>
        </div>
        <div className="text-[11px] text-slate-500">Now: {what}</div>
        <ul className="space-y-1" aria-label="Jobs">
          {jobs.map((j) => (
            <li key={j.id} className="flex items-center gap-1.5 text-xs" data-testid="job">
              <input aria-label={`Name of ${j.name}`} className="field !w-28 px-1.5 py-1 text-xs" value={j.name} onChange={(e) => rename(j.id, e.target.value)} />
              <span className="min-w-0 flex-1 truncate text-slate-400" title={j.what}>{j.what}</span>
              <span data-testid="job-status" className={j.status === 'failed' ? 'text-rose-300' : j.status === 'done' ? 'text-emerald-300' : 'text-slate-400'} title={j.error}>
                {j.status === 'running' ? `running ${Math.round((j.progress ?? 0) * 100)}%` : j.status}
              </span>
              <button type="button" className="chip" disabled={j.status === 'running'} onClick={() => remove(j.id)}>Remove</button>
            </li>
          ))}
        </ul>
        <div className="flex flex-wrap gap-1.5">
          {running
            ? <button type="button" className="chip" onClick={cancel}>Cancel</button>
            : <button type="button" className="chip chip-on" disabled={toRun === 0} onClick={() => runAll()}>Run all ({toRun})</button>}
          {done.length > 0 && <button type="button" className="chip" onClick={() => download('jobs.csv', jobsCsv(jobs), 'text/csv')} title="Every receiver of every job done, its ISO 3382 single numbers and STI">Jobs CSV</button>}
        </div>
        {jobs.some((j) => j.status === 'failed') && <div className="text-[11px] text-rose-300">{jobs.filter((j) => j.error).map((j) => `${j.name}: ${j.error}`).join(' ')}</div>}
      </Section>

      {done.length > 0 && (
        <Section title="Compared at the main receiver">
          <div className="overflow-x-auto">
            <table className="w-full text-xs tabular-nums" data-testid="jobs-compared">
              <thead>
                <tr className="text-slate-400">
                  <th className="py-1 pr-2 text-left font-normal">Single number</th>
                  {done.map((j) => <th key={j.id} className="px-2 py-1 text-right font-medium text-slate-200">{j.name}</th>)}
                </tr>
              </thead>
              <tbody>
                {ISO_ROWS.map((row) => (
                  <tr key={row.key} className="border-t border-white/5 text-slate-200">
                    <th scope="row" className="py-1 pr-2 text-left font-normal">{row.label}{row.unit && <span className="text-slate-500"> ({row.unit})</span>}</th>
                    {done.map((j) => <td key={j.id} className="px-2 py-1 text-right">{cell(j.result!.broadband[row.key], row.digits)}</td>)}
                  </tr>
                ))}
                <tr className="border-t border-white/5 text-slate-200">
                  <th scope="row" className="py-1 pr-2 text-left font-normal">STI</th>
                  {done.map((j) => <td key={j.id} className="px-2 py-1 text-right">{cell(j.result!.sti.sti, 2)}</td>)}
                </tr>
              </tbody>
            </table>
          </div>
        </Section>
      )}
    </>
  );
}
