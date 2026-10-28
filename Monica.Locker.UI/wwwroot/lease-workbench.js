// Owned visibility subscription. Disposal stops callbacks before the .NET target is released.
export function watchVisibility(target) {
    let disposed = false;
    const changed = () => {
        if (!disposed) target.invokeMethodAsync("VisibilityChanged", !document.hidden).catch(() => {});
    };
    document.addEventListener("visibilitychange", changed);
    changed();
    return { dispose() { disposed = true; document.removeEventListener("visibilitychange", changed); } };
}
