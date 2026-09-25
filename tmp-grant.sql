INSERT INTO "TenantMemberships" ("Id", "CustomerAccountId", "TenantId", "Role", "GrantedBy", "CreatedAt")
SELECT gen_random_uuid(), a."Id", t."Id", 'Owner', 'operator: stand administrator', now()
FROM "CustomerAccounts" a, "Tenants" t
WHERE a."Email" = 'remixod@gmail.com'
  AND t."Slug" = 'showcase'
  AND NOT EXISTS (
    SELECT 1 FROM "TenantMemberships" m
    WHERE m."CustomerAccountId" = a."Id" AND m."TenantId" = t."Id"
  );

SELECT t."Slug", m."Role"
FROM "TenantMemberships" m
JOIN "CustomerAccounts" a ON a."Id" = m."CustomerAccountId"
JOIN "Tenants" t ON t."Id" = m."TenantId"
WHERE a."Email" = 'remixod@gmail.com'
ORDER BY t."Slug";
