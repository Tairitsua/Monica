"use strict";

const DEFAULT_MESSAGES = {
    NoArguments: "No arguments",
    NotConnected: "Not connected",
    ConnectionClosedWithError: "Connection closed with error: {0}",
    ConnectionClosed: "Connection closed",
    Reconnecting: "Reconnecting...",
    ReconnectedSuccessfully: "Reconnected successfully",
    ConnectedSuccessfully: "Connected successfully",
    ConnectionFailed: "Connection failed: {0}",
    Disconnected: "Disconnected",
    RegisteredListener: "Registered listener for: {0}",
    UnregisteredListener: "Unregistered listener for: {0}"
};

export async function createSession(callback, localizedMessages) {
    if (!globalThis.signalR) {
        await import("./signalr.min.js");
    }

    const signalRApi = globalThis.signalR;
    let connection = null;
    let callbackReference = callback;
    let isShuttingDown = false;
    let shutdownPromise = null;
    let connectionGeneration = 0;
    const registeredListeners = new Map();
    const pendingCallbacks = new Set();
    const stoppingConnections = new WeakMap();
    const messages = { ...DEFAULT_MESSAGES, ...(localizedMessages || {}) };

    function text(key, ...args) {
        let value = messages[key] || DEFAULT_MESSAGES[key] || key;
        args.forEach((argument, index) => {
            value = value.replaceAll(`{${index}}`, argument ?? "");
        });
        return value;
    }

    function closedResult() {
        return { success: false, error: text("NotConnected") };
    }

    function notify(method, ...args) {
        if (isShuttingDown || !callbackReference) {
            return;
        }

        let callbackPromise;
        try {
            callbackPromise = Promise.resolve(callbackReference.invokeMethodAsync(method, ...args));
        } catch {
            return;
        }

        pendingCallbacks.add(callbackPromise);
        callbackPromise.then(
            () => pendingCallbacks.delete(callbackPromise),
            () => pendingCallbacks.delete(callbackPromise));
    }

    function log(source, content, type) {
        notify("Invoke", source, content, type);
    }

    function detachRegisteredListeners(activeConnection) {
        for (const methodName of registeredListeners.keys()) {
            try {
                activeConnection.off(methodName);
            } catch {
                // Continue detaching the remaining callback producers before teardown.
            }
        }
        registeredListeners.clear();
    }

    function stopConnection(activeConnection) {
        const existingStop = stoppingConnections.get(activeConnection);
        if (existingStop) {
            return existingStop;
        }

        const stopPromise = Promise.resolve()
            .then(() => activeConnection.stop())
            .finally(() => {
                if (stoppingConnections.get(activeConnection) === stopPromise) {
                    stoppingConnections.delete(activeConnection);
                }
            });
        stoppingConnections.set(activeConnection, stopPromise);
        return stopPromise;
    }

    async function disconnectCore(notifyClient) {
        const activeConnection = connection;
        if (activeConnection) {
            connection = null;
            detachRegisteredListeners(activeConnection);
            await stopConnection(activeConnection);
            if (isShuttingDown) {
                return false;
            }
        } else {
            registeredListeners.clear();
        }

        if (notifyClient && !isShuttingDown) {
            notify("OnConnectionStatusChangedForSession", "Disconnected", connectionGeneration);
            notify("SetConnectionIdForSession", "", connectionGeneration);
            log("System", text("Disconnected"), "Info");
        }

        return !isShuttingDown;
    }

    async function connect(hubUrl, accessToken, generation) {
        if (isShuttingDown) {
            return closedResult();
        }

        let activeConnection = null;
        const activeGeneration = generation || 0;
        connectionGeneration = activeGeneration;
        try {
            await disconnectCore(false);
            if (isShuttingDown) {
                return closedResult();
            }

            activeConnection = new signalRApi.HubConnectionBuilder()
                .withUrl(hubUrl, { accessTokenFactory: () => accessToken || "" })
                .configureLogging(signalRApi.LogLevel.Information)
                .withAutomaticReconnect()
                .build();

            activeConnection.onclose(error => {
                if (isShuttingDown || connection !== activeConnection) {
                    return;
                }

                connection = null;
                notify("OnConnectionStatusChangedForSession", "Disconnected", activeGeneration);
                notify("SetConnectionIdForSession", "", activeGeneration);
                log(
                    "System",
                    error ? text("ConnectionClosedWithError", error.message || error) : text("ConnectionClosed"),
                    error ? "Error" : "Info");
            });
            activeConnection.onreconnecting(() => {
                if (isShuttingDown || connection !== activeConnection) {
                    return;
                }

                notify("OnConnectionStatusChangedForSession", "Reconnecting", activeGeneration);
                log("System", text("Reconnecting"), "Info");
            });
            activeConnection.onreconnected(connectionId => {
                if (isShuttingDown || connection !== activeConnection) {
                    return;
                }

                notify("OnConnectionStatusChangedForSession", "Connected", activeGeneration);
                notify("SetConnectionIdForSession", connectionId || "", activeGeneration);
                log("System", text("ReconnectedSuccessfully"), "Success");
            });

            connection = activeConnection;
            await activeConnection.start();
            if (isShuttingDown || connection !== activeConnection) {
                if (connection === activeConnection) {
                    connection = null;
                }
                await stopConnection(activeConnection);
                return closedResult();
            }

            notify("OnConnectionStatusChangedForSession", "Connected", activeGeneration);
            notify("SetConnectionIdForSession", activeConnection.connectionId || "", activeGeneration);
            log("System", text("ConnectedSuccessfully"), "Success");
            return { success: true, connectionId: activeConnection.connectionId || "" };
        } catch (error) {
            if (connection === activeConnection) {
                connection = null;
            }

            if (activeConnection) {
                try {
                    await stopConnection(activeConnection);
                } catch {
                    // Preserve the original connection error after best-effort cleanup.
                }

                if (isShuttingDown) {
                    return closedResult();
                }
            }

            if (isShuttingDown) {
                return closedResult();
            }

            const errorMessage = error?.message || String(error);
            notify("OnConnectionStatusChangedForSession", "Disconnected", activeGeneration);
            log("System", text("ConnectionFailed", errorMessage), "Error");
            return { success: false, error: errorMessage };
        }
    }

    async function disconnect(generation) {
        connectionGeneration = generation || 0;
        try {
            await disconnectCore(true);
            return isShuttingDown ? closedResult() : { success: true };
        } catch (error) {
            return isShuttingDown
                ? closedResult()
                : { success: false, error: error?.message || String(error), details: error?.stack || error?.message || String(error) };
        }
    }

    async function invokeMethod(methodName, args) {
        const activeConnection = connection;
        if (isShuttingDown
            || !activeConnection
            || activeConnection.state !== signalRApi.HubConnectionState.Connected) {
            return closedResult();
        }

        try {
            const invocationArguments = args || [];
            await activeConnection.invoke(methodName, ...invocationArguments);
            if (isShuttingDown || connection !== activeConnection) {
                return closedResult();
            }

            const argumentDisplay = invocationArguments.length > 0
                ? JSON.stringify(invocationArguments)
                : text("NoArguments");
            log("Sent", `${methodName}: ${argumentDisplay}`, "Sent");
            return { success: true };
        } catch (error) {
            return isShuttingDown
                ? closedResult()
                : { success: false, error: error?.message || String(error), details: error?.stack || error?.message || String(error) };
        }
    }

    function sendMessage(userName, message) {
        return invokeMethod("ReceiveTestMessage", [userName, message]);
    }

    function registerListener(methodName, methodDisplayName, hubRoute) {
        const activeConnection = connection;
        const activeGeneration = connectionGeneration;
        if (isShuttingDown || !activeConnection) {
            return closedResult();
        }

        try {
            activeConnection.off(methodName);
            activeConnection.on(methodName, (...args) => {
                if (isShuttingDown || connection !== activeConnection) {
                    return;
                }

                const argumentDisplay = args.length > 0 ? JSON.stringify(args) : text("NoArguments");
                notify("OnMethodReceived", methodName, argumentDisplay, hubRoute || "", activeGeneration);
            });
            registeredListeners.set(methodName, methodDisplayName);
            log("System", text("RegisteredListener", methodDisplayName), "Info");
            return { success: true };
        } catch (error) {
            return { success: false, error: error?.message || String(error), details: error?.stack || error?.message || String(error) };
        }
    }

    function unregisterListener(methodName, methodDisplayName) {
        const activeConnection = connection;
        if (isShuttingDown || !activeConnection) {
            return closedResult();
        }

        try {
            activeConnection.off(methodName);
            registeredListeners.delete(methodName);
            log("System", text("UnregisteredListener", methodDisplayName), "Info");
            return { success: true };
        } catch (error) {
            return { success: false, error: error?.message || String(error), details: error?.stack || error?.message || String(error) };
        }
    }

    async function shutdownCore() {
        isShuttingDown = true;
        let shutdownError = null;

        try {
            await disconnectCore(false);
        } catch (error) {
            shutdownError = error;
        } finally {
            registeredListeners.clear();
        }

        await Promise.allSettled([...pendingCallbacks]);
        callbackReference = null;
        return {
            drained: true,
            error: shutdownError?.message || (shutdownError ? String(shutdownError) : null)
        };
    }

    function shutdown() {
        if (!shutdownPromise) {
            isShuttingDown = true;
            shutdownPromise = shutdownCore();
        }
        return shutdownPromise;
    }

    return {
        connect,
        disconnect,
        invokeMethod,
        sendMessage,
        registerListener,
        unregisterListener,
        shutdown
    };
}
