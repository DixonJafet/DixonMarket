# Recommender Azure infrastructure

`azure-recommender.bicep` describes the existing recommendation-training infrastructure in the `rg-marketdixon-recommender` resource group:

- Basic Azure Container Registry, with its admin account disabled.
- Log Analytics workspace and a Consumption-only Container Apps environment.
- User-assigned managed identity with `AcrPull` on the registry.
- Manual-only Container Apps Job using the existing `20261027` image, 0.25 vCPU, 0.5 GiB, a 30-minute timeout, and no retries.
- SQL connection string passed as a secure parameter and stored as a Container Apps secret.

The template is **not deployed by this project**. It uses the resource names and settings documented for the existing deployment. Deploy it at resource-group scope in the existing resource group; it does not create a resource group. Bicep deployments are declarative and repeatable: with the same resource group and parameters, Azure updates or leaves the named resources in place rather than creating duplicate resources. A deployment can still update settings when the template differs from the live configuration.

Before deploying, confirm the resource names, region, image tag, and settings against the live Azure resources. Supply `marketDixonDbConnectionString` as a secure deployment parameter; do not put the value in this file, a `.bicepparam` file, source control, or command history. Bicep masks secure parameters, but Azure still uses the supplied value to set the job secret.

The template does not build or push the container image, start the job, create a schedule, or manage the application database. The image must already exist in ACR. The job remains manual-only.
