import { expect } from '@playwright/test';
import { controlUrl } from './env';

export interface Clip {
  id: string; name: string; category: string | null; favorite: boolean;
  duration: number; createdAt: string; transcript: string | null;
}
export interface Problem { severity: 'error' | 'warning'; message: string }

/** Seeded clips (FakeBackend.Reset) — referenced by name in the specs. */
export const seed = {
  definitelyB: "He's definitely B",   // CS2, ★, said: "…heard him on the stairs"
  trustMe: 'Trust me',                // Bob, said: "Trust me bro, it's fine"
  goodNight: 'Hyvää yötä',            // Matti, said: "Hyvää yötä kaikille"
  latestReplay: 'Replay 2026-10-07 14-32-09', // no category, newest
  categories: ['Alice', 'Bob', 'CS2', 'Matti'], // Alice has no clips
} as const;

/**
 * Talks to the test host's control API: puts the fake PC into a known state and reads back
 * what the phone asked it to do. Every action the page sends is recorded as "action[:detail]".
 */
export class Backend {
  async reset() { await this.post('/reset'); }

  /** Shows problems in the phone's banner (e.g. Discord not running). */
  async setProblems(problems: Problem[]) { await this.post('/problems', problems); }

  /** Makes the next calls of an action ("save", "play", "trim"…) fail with this message. */
  async fail(action: string, message: string) { await this.post('/fail', { action, message }); }

  /** Makes an action succeed with a warning (e.g. "not in a voice channel"). */
  async warn(action: string, message: string) { await this.post('/warn', { action, message }); }

  async setTranscript(name: string, transcript: string | null) { await this.post('/transcript', { name, transcript }); }

  async calls(): Promise<string[]> { return this.get('/calls'); }
  async clips(): Promise<Clip[]> { return this.get('/clips'); }
  async categories(): Promise<string[]> { return this.get('/categories'); }
  async clip(name: string): Promise<Clip> {
    const clip = (await this.clips()).find(c => c.name === name);
    expect(clip, `clip "${name}" exists`).toBeTruthy();
    return clip!;
  }

  /** Waits until the backend has received a call matching the pattern (calls are async). */
  async expectCall(pattern: string | RegExp) {
    await expect.poll(async () => (await this.calls()).some(c => typeof pattern === 'string' ? c === pattern : pattern.test(c)),
      { message: `backend received ${pattern}` }).toBe(true);
  }

  private async post(path: string, body?: unknown) {
    const res = await fetch(controlUrl + path, {
      method: 'POST',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`control ${path}: HTTP ${res.status}`);
  }

  private async get<T>(path: string): Promise<T> {
    const res = await fetch(controlUrl + path);
    if (!res.ok) throw new Error(`control ${path}: HTTP ${res.status}`);
    return res.json() as Promise<T>;
  }
}
