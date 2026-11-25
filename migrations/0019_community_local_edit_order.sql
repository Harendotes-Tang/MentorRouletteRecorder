-- Entries from older versions keep their position and ID order until edited.
ALTER TABLE community_local ADD COLUMN edit_sequence INTEGER CHECK (edit_sequence > 0);
DROP INDEX community_local_order;
CREATE INDEX community_local_order ON community_local(edit_sequence DESC, position, id);
