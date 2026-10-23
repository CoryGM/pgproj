-- The project's own post-deploy script: runs once per publish, after the shared changes, before any shop.
:r seed-platform.sql
:r record-shared-change.sql
