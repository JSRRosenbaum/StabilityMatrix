# Managed Comfy backend

Stability Matrix can connect its Inference client to a separately managed Comfy backend. Enable **Use remote Comfy backend** in Inference settings and enter the backend's HTTP(S) base URL, for example `http://127.0.0.1:8188`. The endpoint must expose the Graft managed-job API. The client connects directly, opens the editor at that URL, and does not start or restart a local Comfy package for remote connections. A local Comfy package that is already running is left alone; stop it explicitly before switching modes if appropriate.

Managed submissions are journaled locally and reconciled with their original job. An unresolved or active job blocks a new prompt; a failed connection or uncertain acceptance is not treated as permission to submit again. There is no implicit retry or replay. Inspect and reconcile the original job before choosing any explicit action.

## Completed model downloads

Optional **Publish completed model downloads** sends eligible model files only after their download completes successfully. Configure an SSH/rsync destination in `host:/absolute/path` form. Each transfer uses a unique hidden staging path, verifies SHA-256 remotely, then atomically publishes the completed file without replacing an existing different file. Matching existing content is accepted; conflicting content is preserved and reported. Interrupted staging is not reused automatically. Any retry is an explicit user action in the publication controls.

Publication is limited to supported, non-archive model downloads directly within an allowlisted model category, with an approved model-file extension and a safe filename. It does not publish arbitrary files, extracted archives, nested category paths, workflows, inputs, generated outputs, application state, or databases. SSH, rsync, and remote checksum support must already be available; this feature does not install or configure the remote host, mount model directories, or guarantee that a published model is compatible with a particular workflow.

See [Graft](https://github.com/JSRRosenbaum/Graft) for the managed backend integration source and its explicit capabilities and limits. The central Graft repository is private, so access to that link requires authorization. This guide documents client behavior; it is not a claim of runtime acceptance for another deployment.
