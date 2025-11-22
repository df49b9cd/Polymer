-- WORK-010 shard ownership store schema (Postgres) — legacy migration kept for compatibility
CREATE TABLE IF NOT EXISTS shard_records (
    namespace              text                     NOT NULL,
    shard_id               text                     NOT NULL,
    strategy_id            text                     NOT NULL,
    owner_node_id          text                     NOT NULL,
    leader_id              text,
    capacity_hint          double precision         NOT NULL,
    status                 integer                  NOT NULL,
    version                bigint                   NOT NULL,
    checksum               text                     NOT NULL,
    updated_at             timestamptz              NOT NULL,
    change_ticket          text,
    CONSTRAINT pk_shard_records PRIMARY KEY(namespace, shard_id)
);

CREATE TABLE IF NOT EXISTS shard_history (
    id                     bigserial                PRIMARY KEY,
    namespace              text                     NOT NULL,
    shard_id               text                     NOT NULL,
    version                bigint                   NOT NULL,
    strategy_id            text                     NOT NULL,
    owner_node_id          text                     NOT NULL,
    previous_owner_node_id text,
    actor                  text                     NOT NULL,
    reason                 text                     NOT NULL,
    change_ticket          text,
    ownership_delta_percent double precision,
    metadata               text,
    created_at             timestamptz              NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_shard_records_owner ON shard_records(owner_node_id);
CREATE INDEX IF NOT EXISTS idx_shard_records_updated ON shard_records(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_shard_history_shard ON shard_history(namespace, shard_id);
CREATE INDEX IF NOT EXISTS idx_shard_history_version ON shard_history(version);
