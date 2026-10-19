-- In case we want to store the raw request payload .e.g. Octocon
ALTER TABLE import_operations ADD COLUMN raw_payload TEXT;
