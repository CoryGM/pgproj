-- A shop's post-deploy script: runs in each shop's own transaction, after the template's changes to that shop.
-- The included files say when they run; a file without a marker would run on every publish, for every shop.
:r seed-new-shop.sql
:r record-shop-change.sql
