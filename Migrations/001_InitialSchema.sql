-- Initial database schema for Assistant application
-- Run this script to set up the database schema in production

-- User clients table
CREATE TABLE IF NOT EXISTS user_clients (
    username       text    NOT NULL,
    name           text    NOT NULL,
    client_id      text    NOT NULL,
    realm          text    NOT NULL,
    enabled        boolean NOT NULL,
    flow_standard  boolean NOT NULL
);

-- API audit logs table
CREATE TABLE IF NOT EXISTS api_audit_logs (
    id            bigserial    PRIMARY KEY,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    operation_type text        NOT NULL,
    username      text         NOT NULL,
    realm         text         NOT NULL,
    target_id     text         NOT NULL,
    details       text         NULL
);

-- Indexes for user_clients
CREATE UNIQUE INDEX IF NOT EXISTS ix_user_clients_identity 
    ON user_clients(username, client_id, realm);

CREATE INDEX IF NOT EXISTS ix_user_clients_client_realm 
    ON user_clients(client_id, realm);

-- Indexes for api_audit_logs
CREATE INDEX IF NOT EXISTS idx_api_audit_logs_created_at_id 
    ON api_audit_logs (created_at desc, id desc);

CREATE INDEX IF NOT EXISTS idx_api_audit_logs_username 
    ON api_audit_logs (username);

CREATE INDEX IF NOT EXISTS idx_api_audit_logs_operation_type_normalized 
    ON api_audit_logs (upper(coalesce(nullif(split_part(operation_type, ':', 2), ''), operation_type)));
