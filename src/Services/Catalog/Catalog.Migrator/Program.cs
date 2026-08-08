// Applying zero migrations succeeds, so exiting 0 is the truthful outcome:
// no DbContext exists until PR-08, which turns this shell into the §7.4 job
// host — Database.Migrate() and nothing else.
Console.WriteLine("Catalog.Migrator: no migrations exist yet — the §7.4 migration host arrives with PR-08.");
