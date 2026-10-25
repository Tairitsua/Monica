# K8S operations

Use the K8S workspace to inspect namespaces and resources, review concrete restart or replica plans, and execute them through the host's configured remote kubectl connection.

```csharp
builder.AddMonica(monica =>
{
    monica.AddK8S(options =>
    {
        options.RuntimeConfig.ExecutionNode = executionNode;
        options.RuntimeConfig.UserName = sshUser;
        options.RuntimeConfig.Password = sshPassword;
        options.RuntimeConfig.NamespaceScope = ["application"];
        options.RuntimeConfig.SensitiveOperationNameRestrictions = ["application-"];
    });
    monica.AddK8SUI();
});
```

`executionNode`, `sshUser` and `sshPassword` are host-supplied connection settings; obtain credentials through the host's own secret configuration. `AddK8SUI()` requires K8S, Localization and Shell. Complete the web host with `UseMonica()` and `MapMonica()`; see [Shell and access](shell-and-access.md). The K8S route family declares no built-in access policy. Both destinations are owned by `UIK8SPage`, so the shell evaluates every distinct registered policy for that component before rendering either workbench. An effective host policy guarding the inventory component also guards runtime configuration. The host owns authentication and authorization for connection settings and remote mutations, including any enabled K8S Minimal APIs. Hiding a navigation item is not access control. Shell navigation contributions append registrations; duplicate routes fail rather than overriding an existing page's policy.

The operations provider is `SshRemoteKubectlProvider`. It connects by SSH password authentication and runs kubectl on `ExecutionNode`; it does not use a browser-side Kubernetes client. `ModuleK8SOption` defaults to SSH port `22`, command and connection timeout `30` seconds, and executable text `kubectl`. These provider options are host registration settings, not fields in the runtime editor. The configured master and worker addresses are metadata and execution-node fallbacks, not discovered topology or health evidence.

## Publish the current Service's NodePort addresses

For a host running inside Kubernetes, opt in to publishing its Service's external NodePort addresses to a process environment variable:

```csharp
builder.AddMonica(monica =>
{
    monica.AddK8S(options => options.NodePortAddresses.Enabled = true);
});
```

`ModuleK8SOption.NodePortAddresses` is a `K8SNodePortAddressOptions` value with these defaults:

| Option | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `false` | Starts one background initialization attempt when the host starts. |
| `ServiceNameEnvironmentVariable` | `K8S_SERVICE_NAME` | Contains the exact name of this application's Kubernetes Service. |
| `BaseAddressEnvironmentVariable` | `K8S_HOST_IP` | Contains the externally reachable host or IP combined with each NodePort. |
| `ListeningAddressesEnvironmentVariable` | `K8S_LISTENING_ADDRESSES` | Receives the semicolon-separated result. |

Supply the Service name and base-address environment variables in the workload. Initialization reads `KUBERNETES_SERVICE_HOST` and `KUBERNETES_SERVICE_PORT_HTTPS`, the mounted ServiceAccount token and CA certificate, and the namespace file under `/var/run/secrets/kubernetes.io/serviceaccount/`; the namespace falls back to `default` when its file is absent. The reader trims the token before setting the Bearer header so trailing whitespace in the mounted file does not invalidate the request. The Pod's actual ServiceAccount must already have `get` permission for the target Service in that namespace. Check the account selected by the workload against the RBAC binding; a binding for `default` does not grant access to a custom ServiceAccount. Migrating an initializer that already reads this same Service needs no additional Kubernetes permission.

This capability calls the Kubernetes API directly with the workload's ServiceAccount. It uses neither SSH credentials nor the runtime editor's connection settings and does not require `AddK8SUI()` or service discovery. The result contains `http://{baseAddress}:{nodePort}` for each port of a `NodePort` Service, in returned order, joined with `;`. A successful attempt replaces the configured output environment variable. A present but empty `ports` array is a successful empty result: publication clears the output environment variable, preserving the previous initializer's behavior.

Missing settings, inaccessible files, request failures, unsupported Service types, or malformed responses leave the previous output value intact. These failures are logged; a response missing `spec`, `type`, or `ports` is ignored without publication. Initialization runs once and does not track later Service changes.

The host starts the initializer as a one-shot `BackgroundService` and does not wait for the Kubernetes query before continuing startup. Shutdown cancels an in-flight query, and a canceled result is not published. There is no publication-order guarantee relative to other hosted services: service discovery may register before the output is available and picks it up on a later heartbeat under its [address metadata contract](../../monica-infra-hosting/references/service-discovery.md#address-and-environment-metadata). Publication changes address metadata without changing Kestrel bindings, `IServerAddressesFeature`, or the system-information page's captured listening endpoints.

When moving an existing application initializer into `AddK8S`, map its existing enabled value and all three environment-variable names to `NodePortAddresses`; do not replace application defaults with the module's opt-in default. Keep any existing service-discovery metadata and display allow lists under that contract.

For cluster acceptance, start without a prefilled output environment variable: preserving a previous value after failure must not be mistaken for a successful query. Verify that the expected NodePort addresses appear, then observe `LastHeartbeatTime` advance and confirm that the registration still contains the address metadata. Reader replacements in automated tests do not verify the cluster's mounted credentials, network access, or RBAC.

## Inventory and runtime configuration

The registered peer routes are `/ui/k8s` for inventory and `/ui/k8s/runtime-config` for runtime configuration. `UIK8SPage` declares both exact Blazor routes and composes the selected workbench; `UIK8SRuntimeConfigPage` is an embedded content wrapper with ordinary parameters, not an independently routed page. Route selection respects the host's path base and ignores query strings, fragments, trailing slashes and casing. Switching destinations retires the previous workbench and its visit controller. Their shared workbench navigation uses real links, so direct loads, reloads and browser history retain the destination. Inventory supports Services, Deployments, StatefulSets and DaemonSets, with native sort, property filters, search, selection and pagination. An accepted resource capture retains its namespace and kind. A failed attempted read does not rename retained evidence, and a failed read is distinct from a successful empty list.

`UIK8SPage.ShellRouteContext` consumes the shell's authorized `RouteData` cascade so peer navigation and Back/Forward rerender the correct workbench even when both exact routes have no route parameters and their query hints are identical. Custom routing follows the [shell route-context contract](shell-and-access.md). Query-only updates retain the current workbench rather than resetting the runtime configuration draft.

Nonsecret `ns`, `kind` and `q` URL hints are revalidated when the destination is adopted. Local inventory controls update visit preferences and outgoing peer links; they do not replace the existing browser-history entry. Back therefore adopts that entry's explicit hints, which can differ from the most recent local control choice. A hinted inventory link, reload and history traversal retain those hinted preferences independently of the runtime editor draft.

Selection uses namespace, resource kind and exact name rather than model-reference equality. A reread keeps matching selected identities and projects their current evidence; missing resources are pruned, and namespace/kind changes reset selection. Search and property filters can hide selected targets without changing their captured identity or operation scope. Explicit Clear remains available when idle.

Resource inspection separates Summary, Pods and exact kubectl Describe text. Pod phase, readiness and issue evidence are independent; a Running phase does not prove readiness, and `HasIssue` does not change the reported phase. Nested Pod inspection uses that Pod's captured namespace and name. The owner returned by Pod details may be a ReplicaSet rather than the resolved Deployment shown in the resource inspector.

Runtime configuration edits a detached draft. Save replaces the process-wide `IK8SRuntimeConfigStore` value for this running application; it is not durable across process restart and does not test the connection. Discard reloads the current value, which may have changed in another circuit. A failed reload retains the draft. Password fields remain masked and credentials are excluded from browser preference storage and inventory summaries. Namespace, resource-kind and search preferences can persist independently of the editor.

`NamespaceScope` is an allow list; an empty list imposes no namespace restriction. Namespace discovery includes configured names that the provider did not find, marked as missing rather than reported as existing. `SensitiveOperationNameRestrictions` allows a mutation when the requested target name contains at least one configured keyword, ignoring case; an empty list imposes no name restriction. Service requests check the requested Service name, then resolve backing workloads. These checks do not replace Kubernetes or SSH authorization.

## Preview, execution and outcomes

Namespace actions are **Service-backed** regardless of the currently displayed kind: they discover Services, resolve their backing workloads and deduplicate shared workloads. They exclude orphan workloads. A selected workload action addresses the captured names of that kind directly, so an orphan can be eligible. DaemonSets support restart but not replica scaling.

The UI captures namespace, kind, names and operation before requesting a preview. Keep the returned requested, eligible and ignored targets, ignored reasons, workload identities and command arguments visible. The preview is advisory: a later execution captures the then-current runtime configuration and discovers and checks targets again. It does not bind the future connection or grant permission to execute.

Native confirmation labels describe the operation and concrete workload count. The public restart and scale dialogs retain an assignable `ConfirmButtonText` parameter for source compatibility, but it does not override that label. Embedded callers supply the actual preview and use the native derived confirmation.

Within each public read, preview or execution operation, the owning service captures one detached configuration snapshot and reuses it through discovery, planning and commands. A concurrent save cannot switch that call to another execution node or restriction set halfway through. Separate public calls capture separately. This consistency does not freeze Kubernetes resource state or make multiple commands atomic.

ScaleDown records the current desired replica count in `monica.devops/previous-replicas` before scaling to zero. ScaleUp restores a positive recorded count or falls back to one, and never reduces a workload already above the restoration target. Annotation and scale are separate commands; a later failure can follow an applied annotation or earlier successful workload command. The existing facade maps a `K8SOperationException` to `BadRequest`; preserve its localized reason rather than infer a different authorization status.

Display actual `K8SRestartResult` or `K8SScaleResult` evidence: `Namespace`, `ScopeName`, `ResourceType`, `AffectedTargets`, `Workloads`, `ExecutedCommands`, `IgnoredTargets` and `CompletedAtUtc`, including actual replica plans for scaling. **Commands completed** means those commands returned successfully. It does not establish controller convergence, Pod readiness or rollout completion. A failed execution does not return a complete partial-success receipt; inspect current resources before deciding to retry. Keep a subsequent refresh failure separate from the command outcome.

For a failed execution, the UI identifies the attempted operation and scope from its captured request, explicitly as attempted context. It does not replace that context with current controls or claim preview targets were affected. Successful scope and effect details remain the backend result's evidence.

The route owns its admitted reads, mutation callbacks and native inspectors. Retirement cancels where supported and drains the actual producers and late dialog references before releasing the owner. The synchronous SSH command inside the provider can outlive cancellation; a cancellation request alone is not completion evidence. Injected facades and services remain borrowed. Component authors should follow the async lifetime contract in `$monica-ui-development` (`references/blazor-best-practices.md#22-bound-async-work-to-the-component-lifetime`).
