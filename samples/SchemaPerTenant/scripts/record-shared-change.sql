-- pgproj: run when=changed
-- Runs only when the shared schemas have changes in this publish.
INSERT INTO platform.shop_events (shop, event) VALUES ('*', 'shared schemas changed');
