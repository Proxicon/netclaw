# Proxicon Development Deployment In Komodo

## Scope

This procedure deploys the Proxicon Netclaw fork to the Bostec development host.

- Build resource: `netclaw-proxicon-dev-build`
- Deployment resource: `netclaw-proxicon-dev`
- Procedure resource: `netclaw-proxicon-dev-release`
- Builder: Komodo `Local`
- Deployment server: `btcavdkr02` at `10.99.10.121`
- Container network: `host`
- Daemon port: `5199`

Komodo must build the image on `Local`.
CT108 has the `NOBUILD` tag and must only pull and run images.

## Image Build

The build uses `docker/Dockerfile` with these build arguments:

```text
ARTIFACTS_STAGE=source-artifacts
TARGETARCH=amd64
```

The source stage publishes the CLI and daemon inside the Komodo builder.
The normal release path still uses the pre-built artifact stage.

The build must publish version and commit tags to:

```text
git.boston.net.za/bostec/netclaw-proxicon-dev
```

Do not use `latest` as the deployment record.
Komodo must deploy the build version that the procedure produced.

## Runtime State And Secrets

The deployment mounts this host directory:

```text
/opt/netclaw/proxicon-dev:/home/netclaw/.netclaw
```

The image entrypoint sets the mount owner to UID and GID `1654`.
Komodo must inject provider and Teams credentials as managed secrets.
Do not put credentials in this repository, the Dockerfile, or Traefik.

The deployment must set these non-secret values:

```text
NETCLAW_Daemon__Host=0.0.0.0
NETCLAW_Daemon__Port=5199
```

## Manual Release Procedure

1. Confirm CT108 has at least 8 GB free.
2. Run `netclaw-proxicon-dev-release` in Komodo.
3. The procedure runs the build on `Local`.
4. The procedure deploys the new image on `btcavdkr02`.
5. Confirm `http://127.0.0.1:5199/api/health/ready` on CT108.
6. Confirm Traefik can reach `10.99.10.121:5199`.
7. Add the narrow Teams callback route only after the health checks pass.

## Rollback

Select the prior immutable build version in the deployment.
Redeploy the selected version through Komodo.
Do not remove the state mount during rollback.
