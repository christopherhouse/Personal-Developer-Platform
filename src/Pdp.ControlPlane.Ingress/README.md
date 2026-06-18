# Pdp.ControlPlane.Ingress

Control plane (spec 006). The **one public surface** (constitution Article IX): a pure
`Yarp.ReverseProxy` that forwards `POST /webhooks/github` to the internal `Pdp.ControlPlane.Api`. No
business logic, no project references.

> Production ACA hosting + ingress + the App Insights resource = **spec 007**; this is the sanctioned
> public ingress that spec deploys.
