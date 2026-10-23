:r ./Seed/PrincipalSeed.sql
:r ./Seed/RoleSeed.sql
:r ./Seed/PrincipalRoleSeed.sql
:r ./Seed/CatalogSeed.sql
:r ./Seed/InventorySeed.sql
:r ./Seed/SalesSeed.sql
:r ./Seed/BillingSeed.sql
call reporting.refresh_materialized_views();
