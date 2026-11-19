# Customers example

The smallest example, and the one the [Quickstart](../../README.md#quickstart) uses: one
library, `DEMOLIB`, with a customer maintenance program.

| Member | Type | What it is |
|---|---|---|
| customers.sql | SQL | Creates the CUSTOMERS table and seeds 50 customers |
| custmntd.dspf | DSPF | The menu, the customer list (a subfile), lookup, new customer and detail screens |
| custmnt.sqlrpgle | SQLRPGLE | CUSTMNT, the program |

Mount this folder as the source folder, sign on at port 2323 and enter `CALL DEMOLIB/CUSTMNT`.
From the menu, 1 lists or searches customers by name, 2 looks one up by number, 3 creates a
customer and 4 prints one to a spooled file named CUSTPRT. F3 exits.
