# Trusted public keys

Every `*.pub` file here is compiled into the updater:

- `patch*.pub` keys may sign game channels (`channel.json`).
- `updater*.pub` keys may sign the updater feed (`updater.json`).

Private keys (`*.pem`) never go in this repo. The `.gitignore` blocks them. See
[docs/release-flow.md](../docs/release-flow.md#keys) for where the private keys live and how to
rotate one.
