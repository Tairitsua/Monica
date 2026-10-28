---
name: monica-infra-messaging
description: Use when integrating Monica EventBus, API clients, DataChannel pipelines, or SignalR hubs and diagnosing their transport, delivery, and provider configuration.
---

# Monica messaging

Choose the communication boundary before the transport; database-coupled delivery also needs the persistence skill's [transactional events](../monica-infra-persistence/references/transactional-events.md).

| Task | Read |
| --- | --- |
| Notify typed handlers, classify rejected deliveries, or configure dead-letter handling | [EventBus](references/event-bus.md) |
| Inspect EventBus subscriptions, transport status, providers, or diagnostic listeners | [EventBus diagnostics](references/event-bus-diagnostics.md) |
| Configure the Kafka console or inspect broker clusters, topics, consumer groups, and offset samples | [Kafka console](references/kafka-console.md) |
| Call a published request, select Shared/Independent participation, or route by role | [API client](references/invocation.md) |
| Recover structured remote failures from Dapr actor calls | [Dapr diagnostics](references/dapr-diagnostics.md) |
| Send through or inspect a named bidirectional pipeline owned by one host | [DataChannel](references/data-channels.md) |
| Invoke typed methods on connected Web clients or inspect a browser debug session | [SignalR](references/signalr.md) |

For application contract placement, use ProjectUnit development in `$monica-application-project-unit-development`. For framework transport or module implementation, use `$monica-framework`, then `$monica-development`.

If a named skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md` and specified reference.
