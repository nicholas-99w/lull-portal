-- One row (id = 1): IT's branding for the portal (Branding, as JSON: the
-- school's name, colours and words for parents) and its logo, a PNG, JPEG or
-- WebP image of at most 512 KB. No row: Lull's own look.
CREATE TABLE IF NOT EXISTS branding (
    id         int PRIMARY KEY CHECK (id = 1),
    settings   jsonb NOT NULL,
    logo       bytea,
    logo_type  text,
    updated_by text NOT NULL,
    updated_at timestamptz NOT NULL
);
