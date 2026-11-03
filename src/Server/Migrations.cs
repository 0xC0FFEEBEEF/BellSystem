namespace SchoolBell.Server;

/// Ordered code-first migrations. Each entry is one transaction. Appended-to list only.
public static class Migrations
{
    public static readonly string[] Sql =
    [
        // 1: identity, sessions, enrollment, devices
        """
        CREATE TABLE users(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          username TEXT NOT NULL UNIQUE,
          pw_hash TEXT NOT NULL,
          role TEXT NOT NULL CHECK(role IN ('admin','operator','viewer')),
          group_scope TEXT NOT NULL DEFAULT '[]',
          schoolwide_broadcast INTEGER NOT NULL DEFAULT 0,
          failed_attempts INTEGER NOT NULL DEFAULT 0,
          lockout_until INTEGER,
          created_at INTEGER NOT NULL
        );
        CREATE TABLE sessions(
          id TEXT PRIMARY KEY,
          user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
          created INTEGER NOT NULL,
          expires INTEGER NOT NULL
        );
        CREATE INDEX idx_sessions_expires ON sessions(expires);
        CREATE TABLE enrollment_tokens(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          token_hash TEXT NOT NULL UNIQUE,
          expires_at INTEGER NOT NULL,
          max_uses INTEGER NOT NULL DEFAULT 1,
          used INTEGER NOT NULL DEFAULT 0,
          group_scope TEXT NOT NULL DEFAULT '[]',
          created_by TEXT,
          label TEXT NOT NULL,
          revoked INTEGER NOT NULL DEFAULT 0,
          created_at INTEGER NOT NULL
        );
        CREATE TABLE devices(
          device_id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          secret TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'active',
          revoked INTEGER NOT NULL DEFAULT 0,
          first_seen INTEGER NOT NULL,
          last_seen INTEGER,
          accepted_revision INTEGER NOT NULL DEFAULT 0,
          cache_revision INTEGER NOT NULL DEFAULT 0,
          cache_expires_at INTEGER,
          last_cursor TEXT,
          computer_name TEXT,
          active_user TEXT,
          active_sid TEXT,
          status_json TEXT,
          keep_awake_json TEXT
        );
        CREATE INDEX idx_devices_name ON devices(name);
        CREATE INDEX idx_devices_last_seen ON devices(last_seen);
        CREATE TABLE groups(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
        CREATE TABLE group_members(
          device_id TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
          group_id INTEGER NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
          PRIMARY KEY(device_id, group_id)
        );
        CREATE INDEX idx_group_members_group ON group_members(group_id);
        """,

        // 2: schedules, calendar, published revisions, occurrences, sounds
        """
        CREATE TABLE schedule_templates(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          name TEXT NOT NULL UNIQUE,
          target TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        CREATE TABLE template_periods(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          template_id INTEGER NOT NULL REFERENCES schedule_templates(id) ON DELETE CASCADE,
          period_index INTEGER NOT NULL,
          name TEXT NOT NULL,
          start_hm TEXT NOT NULL,
          end_hm TEXT NOT NULL,
          warn_min INTEGER NOT NULL DEFAULT 0,
          sound_id TEXT NOT NULL,
          volume_policy TEXT NOT NULL DEFAULT 'announcement',
          duration_s INTEGER NOT NULL DEFAULT 3,
          UNIQUE(template_id, period_index)
        );
        CREATE TABLE weekday_assignments(
          template_id INTEGER NOT NULL REFERENCES schedule_templates(id) ON DELETE CASCADE,
          day INTEGER NOT NULL CHECK(day BETWEEN 0 AND 6),
          PRIMARY KEY(template_id, day)
        );
        CREATE TABLE date_overrides(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          date TEXT NOT NULL UNIQUE,
          template_id INTEGER REFERENCES schedule_templates(id) ON DELETE CASCADE
        );
        CREATE TABLE holidays(date TEXT PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE published_revisions(
          revision INTEGER PRIMARY KEY AUTOINCREMENT,
          manifest_json TEXT NOT NULL,
          signing_key_ref TEXT NOT NULL,
          published_at INTEGER NOT NULL,
          published_by TEXT NOT NULL
        );
        CREATE TABLE occurrences(
          revision INTEGER NOT NULL REFERENCES published_revisions(revision) ON DELETE CASCADE,
          occurrence_id TEXT NOT NULL,
          starts_at INTEGER NOT NULL,
          bell_name TEXT NOT NULL,
          kind TEXT NOT NULL,
          sound_id TEXT NOT NULL,
          volume_policy TEXT NOT NULL,
          duration_s INTEGER NOT NULL,
          target_key TEXT NOT NULL,
          template_name TEXT NOT NULL,
          local_date TEXT NOT NULL,
          local_hm TEXT NOT NULL,
          PRIMARY KEY(revision, occurrence_id)
        );
        CREATE INDEX idx_occurrences_starts ON occurrences(starts_at);
        CREATE TABLE sound_assets(
          id TEXT PRIMARY KEY,
          sha256 TEXT NOT NULL,
          file_path TEXT NOT NULL,
          bytes INTEGER NOT NULL
        );
        """,

        // 3: outbox, receipts, audit, keep-awake policies
        """
        CREATE TABLE outbox(
          seq INTEGER PRIMARY KEY AUTOINCREMENT,
          device_id TEXT NOT NULL REFERENCES devices(device_id) ON DELETE CASCADE,
          event_id TEXT NOT NULL,
          event_json TEXT NOT NULL,
          created_at INTEGER NOT NULL
        );
        CREATE INDEX idx_outbox_device_seq ON outbox(device_id, seq);
        CREATE UNIQUE INDEX idx_outbox_device_event ON outbox(device_id, event_id);
        CREATE TABLE receipts(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          device_id TEXT NOT NULL,
          ref_id TEXT NOT NULL,
          state TEXT NOT NULL,
          at INTEGER NOT NULL,
          error_code TEXT,
          created_at INTEGER NOT NULL,
          UNIQUE(device_id, ref_id, state)
        );
        CREATE INDEX idx_receipts_ref ON receipts(ref_id);
        CREATE INDEX idx_receipts_at ON receipts(at);
        CREATE TABLE audit(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          actor TEXT NOT NULL,
          action TEXT NOT NULL,
          detail TEXT,
          at INTEGER NOT NULL
        );
        CREATE TABLE keepawake_policies(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          policy_id TEXT NOT NULL UNIQUE,
          scope TEXT NOT NULL CHECK(scope IN ('device','group','all')),
          target_id TEXT,
          mode TEXT NOT NULL DEFAULT 'until',
          expires_at INTEGER NOT NULL,
          display_off INTEGER NOT NULL DEFAULT 0,
          released INTEGER NOT NULL DEFAULT 0,
          created_by TEXT,
          created_at INTEGER NOT NULL
        );
        CREATE TABLE keepawake_acks(
          policy_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          applied INTEGER NOT NULL,
          applied_at INTEGER NOT NULL,
          PRIMARY KEY(policy_id, device_id)
        );
        """,

        // 4: milestone S2 — announcements, talk leases, audience snapshots, delivery results,
        //    media tokens, transcripts + durable transcription queue
        """
        CREATE TABLE announcements(
          announcement_id TEXT PRIMARY KEY,
          kind TEXT NOT NULL CHECK(kind IN ('live','recorded')),
          status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','ended')),
          created_by TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          started_at INTEGER,
          window_closes_at INTEGER,
          ended_at INTEGER,
          ended_by TEXT,
          max_duration_s INTEGER NOT NULL,
          offer_expires_at INTEGER NOT NULL,
          chime INTEGER NOT NULL DEFAULT 0,
          target_json TEXT NOT NULL,
          target_label TEXT NOT NULL,
          audience_snapshot_id TEXT NOT NULL,
          lease_id TEXT,
          source_label TEXT NOT NULL,
          recording_path TEXT,
          recording_sha256 TEXT,
          recording_bytes INTEGER,
          recording_duration_ms INTEGER,
          recording_sample_rate INTEGER,
          frames_sent INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX idx_announcements_created ON announcements(created_at DESC);
        CREATE INDEX idx_announcements_status ON announcements(status, kind);
        CREATE TABLE announcement_devices(
          announcement_id TEXT NOT NULL REFERENCES announcements(announcement_id) ON DELETE CASCADE,
          device_id TEXT NOT NULL,
          snapshot_id TEXT NOT NULL,
          label TEXT NOT NULL,
          PRIMARY KEY(announcement_id, device_id)
        );
        CREATE TABLE talk_leases(
          lease_id TEXT PRIMARY KEY,
          username TEXT NOT NULL,
          target_json TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          expires_at INTEGER NOT NULL,
          renew_interval_s INTEGER NOT NULL,
          renew_deadline INTEGER NOT NULL,
          status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','released','expired')),
          ended_reason TEXT,
          announcement_id TEXT
        );
        CREATE INDEX idx_leases_status ON talk_leases(status, expires_at);
        CREATE TABLE media_tokens(
          announcement_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          token_hash TEXT NOT NULL,
          expires_at INTEGER NOT NULL,
          issued_at INTEGER NOT NULL,
          used INTEGER NOT NULL DEFAULT 0,
          used_at INTEGER,
          PRIMARY KEY(announcement_id, device_id)
        );
        CREATE TABLE delivery_results(
          announcement_id TEXT NOT NULL REFERENCES announcements(announcement_id) ON DELETE CASCADE,
          device_id TEXT NOT NULL,
          state TEXT NOT NULL,
          error_code TEXT,
          source TEXT NOT NULL DEFAULT 'server',
          updated_at INTEGER NOT NULL,
          PRIMARY KEY(announcement_id, device_id, state)
        );
        CREATE INDEX idx_delivery_announcement ON delivery_results(announcement_id);
        CREATE INDEX idx_delivery_device ON delivery_results(device_id);
        CREATE TABLE transcripts(
          announcement_id TEXT PRIMARY KEY REFERENCES announcements(announcement_id) ON DELETE CASCADE,
          transcript_original TEXT,
          transcript_current TEXT,
          status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','failed','completed')),
          engine TEXT,
          corrected_by TEXT,
          corrected_at INTEGER,
          purged INTEGER NOT NULL DEFAULT 0,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        CREATE TABLE transcript_jobs(
          job_id INTEGER PRIMARY KEY AUTOINCREMENT,
          announcement_id TEXT NOT NULL REFERENCES announcements(announcement_id) ON DELETE CASCADE,
          status TEXT NOT NULL DEFAULT 'queued' CHECK(status IN ('queued','running','completed','failed')),
          attempts INTEGER NOT NULL DEFAULT 0,
          max_attempts INTEGER NOT NULL DEFAULT 5,
          last_error TEXT,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        CREATE INDEX idx_transcript_jobs_status ON transcript_jobs(status, job_id);
        """,

        // 5: ring now, per-device settings, sound library metadata, client update rollouts
        """
        CREATE TABLE bell_rings(
          ring_id TEXT PRIMARY KEY,
          requested_by TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          expires_at INTEGER NOT NULL,
          bell_name TEXT NOT NULL,
          sound_id TEXT NOT NULL,
          sound_sha256 TEXT NOT NULL,
          duration_s INTEGER NOT NULL,
          volume_policy TEXT NOT NULL,
          target_json TEXT NOT NULL,
          target_label TEXT NOT NULL
        );
        CREATE INDEX idx_rings_created ON bell_rings(created_at DESC);
        CREATE TABLE ring_results(
          ring_id TEXT NOT NULL REFERENCES bell_rings(ring_id) ON DELETE CASCADE,
          device_id TEXT NOT NULL,
          label TEXT NOT NULL,
          state TEXT NOT NULL,
          error_code TEXT,
          updated_at INTEGER NOT NULL,
          PRIMARY KEY(ring_id, device_id)
        );
        ALTER TABLE devices ADD COLUMN output_device TEXT;
        ALTER TABLE devices ADD COLUMN announcement_volume INTEGER;
        ALTER TABLE devices ADD COLUMN volume_cap INTEGER;
        ALTER TABLE devices ADD COLUMN room TEXT;
        ALTER TABLE devices ADD COLUMN settings_version INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE devices ADD COLUMN client_version TEXT;
        ALTER TABLE devices ADD COLUMN update_json TEXT;
        ALTER TABLE sound_assets ADD COLUMN name TEXT;
        ALTER TABLE sound_assets ADD COLUMN duration_ms INTEGER;
        ALTER TABLE sound_assets ADD COLUMN builtin INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sound_assets ADD COLUMN updated_at INTEGER;
        ALTER TABLE sound_assets ADD COLUMN updated_by TEXT;
        CREATE TABLE client_rollouts(
          rollout_id TEXT PRIMARY KEY,
          version TEXT NOT NULL,
          created_by TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          target_json TEXT NOT NULL,
          batch_size INTEGER NOT NULL,
          batch_interval_s INTEGER NOT NULL,
          allow_downgrade INTEGER NOT NULL DEFAULT 0,
          status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','completed','cancelled'))
        );
        CREATE TABLE client_rollout_devices(
          rollout_id TEXT NOT NULL REFERENCES client_rollouts(rollout_id) ON DELETE CASCADE,
          device_id TEXT NOT NULL,
          batch INTEGER NOT NULL,
          not_before INTEGER NOT NULL,
          offered_at INTEGER,
          state TEXT NOT NULL DEFAULT 'pending',
          error TEXT,
          updated_at INTEGER NOT NULL,
          PRIMARY KEY(rollout_id, device_id)
        );
        """,
    ];
}
