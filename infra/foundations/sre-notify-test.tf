# TEMPORARY — deliberate validate failure to exercise the notify-sre-agent failure hook
# end-to-end (broken-TF PR → iac-plan fails → SRE Agent HTTP trigger). Never merge;
# delete this file and close the PR once the agent thread is confirmed.
locals {
  sre_agent_notify_test = var.deliberately_undeclared_variable
}
