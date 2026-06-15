# DELIBERATE FAILURE — FR-016 contract drill (T028 / quickstart Scenario 5).
# This passes `tofu validate`/`plan` (valid HCL, "1 to add") but FAILS at apply: the
# principal_id is a well-formed GUID that matches no Entra object, so Azure rejects the
# role assignment with PrincipalNotFound. Nothing else is touched. Removed by the
# fix-forward PR that proves recovery.
resource "azurerm_role_assignment" "fr016_failtest" {
  scope                = azurerm_resource_group.foundations.id
  role_definition_name = "Reader"
  principal_id         = "deadbeef-dead-beef-dead-beefdeadbeef"
}
