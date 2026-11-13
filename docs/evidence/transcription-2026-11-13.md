# Transcription checks — November 13, 2026

Local implementation; no release or school deployment performed during these checks.

## Production evidence supplied by the owner

- Whisper service running, `--threads 12 --processors 1`, `small.en`, CPU-only.
- systemd `CPUQuotaPerSecUSec=12s`; roughly 2.238 seconds accumulated CPU after six minutes.
- Recent recorded announcements in History had no transcript row, rather than a failed inference.
- Code inspection: `StartRecorded` stored/served audio but only `End` queued transcription; recorded
  offers remained active for the default 24 hours. A CPU allowance could not fix missing jobs.

## Regression coverage

- Upload queues a pending transcript immediately while the delivery offer remains active.
- The worker produces a completed transcript while that offer is still active.
- Startup backfills missing recorded transcripts and reclaims jobs interrupted at their final attempt.
- Empty engine output and inference timeouts remain pending/queued with a retry error.
- Service cancellation releases the claim without consuming an attempt.
- Explicit redo works during the recorded delivery window and preserves text until success.
- Quiet/nonzero and clipped PCM reach Whisper; bounded gain does not mutate the supplied WAV.
- Model health requires `/health` with `status=ok`; previews do not queue behind final work.
- An audio-end event or late preview cannot overwrite completed/failed classroom transcript state;
  private transcripts stay office-only, and redo updates the same view.

## Real local speech checks

Engine: pinned whisper.cpp v1.9.5, bundled FP16 `small.en`; selected Haswell CPU backend.
Machine: Ryzen 5 4500U, six cores. Completed trials used four inference threads, one processor,
English, temperature 0.0, temperature increment 0.2, JSON response format.

Input: the dependency's public 11-second JFK sample (`samples/jfk.wav`).

| Input | Result | Observed request time |
| --- | --- | --- |
| Original level | Correct spoken sentence | 9.69 s |
| Reduced amplitude by 1024×, peak 25/32768 | `[Inaudible]` | 12.33 s |
| Same quiet speech with 32× gain, peak about 802/32768 | Correct spoken sentence | 16.02 s |

These are individual local smoke checks, not a speed or accuracy guarantee for the school's VM,
room microphones, names or background noise. Background development tests were also running during
the quiet-audio trials. An initial 12-thread trial oversubscribed this six-core laptop and was
aborted after 90 seconds; that is not a valid comparison to the school's 24-CPU server. The temporary
local inference service was stopped after testing. No classroom recording was exported or modified.

## Owner-supplied recording

The owner subsequently supplied the actual recording associated with the missing transcript.
The source was read locally and left unchanged; its speech content is not included in this repository.

- Valid 16 kHz, mono, 16-bit PCM WAV; duration 21.52 seconds.
- Peak 32724/32768, RMS −23.81 dBFS; zero digitally clipped samples.
- The original quiet-signal skip would not have triggered, and the new inference gain is 1×.
- The same bundled `small.en` engine, four local threads, recognized the announcement in **6.412 s**
  using the application's English/temperature/JSON request settings.
- This verifies that this supplied recording can reach recognition unchanged. It supports the
  missing-job diagnosis for `transcript: none`; it does not measure performance on the school server
  or establish word-perfect transcription accuracy. The temporary local engine was stopped again.
