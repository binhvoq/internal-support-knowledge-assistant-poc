# Bootstrap principal validation — multi-role trên cùng principal chỉ cho PoC demo.

check "bootstrap_principals_unique" {
  assert {
    condition = !var.enable_entra_identity || var.allow_bootstrap_multi_role_principal || length(distinct(values(local.bootstrap_principals))) == length(values(local.bootstrap_principals))
    error_message = "Bootstrap gan cung principal cho nhieu role. Dat allow_bootstrap_multi_role_principal=true (PoC) hoac set bootstrap_*_principal_id rieng."
  }
}
