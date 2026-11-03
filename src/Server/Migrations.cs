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
    ];
}
