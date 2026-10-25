const observers = new WeakMap();

export function observe(root, target) {
    if (observers.has(root)) return;
    const abort = new AbortController();
    const state = { abort, retired: false, frame: 0, labelFrame: 0, pending: null, active: null, lane: null, percent: 50 };
    observers.set(root, state);
    const fitLabels = () => {
        state.labelFrame = 0;
        if (state.retired) return;
        for (const axis of root.querySelectorAll('.lifeline-ticks')) {
            const edge = axis.getBoundingClientRect();
            const now = axis.querySelector('.is-now');
            const right = now ? now.getBoundingClientRect().left - 12 : edge.right;
            let previousRight = edge.left - 12;
            for (const label of axis.querySelectorAll('span:not(.is-now)')) {
                const box = label.getBoundingClientRect();
                const fits = box.left >= previousRight + 12 && box.right <= right;
                label.style.visibility = fits ? 'visible' : 'hidden';
                if (fits) previousRight = box.right;
            }
        }
    };
    const scheduleLabels = () => {
        if (!state.retired && !state.labelFrame) state.labelFrame = requestAnimationFrame(fitLabels);
    };
    state.resize = new ResizeObserver(scheduleLabels);
    state.resize.observe(root);
    state.mutations = new MutationObserver(scheduleLabels);
    state.mutations.observe(root, { childList: true, characterData: true, subtree: true });
    document.fonts.ready.then(scheduleLabels);
    scheduleLabels();
    const flush = () => {
        state.frame = 0;
        if (state.retired || state.active || !state.pending) return;
        const [lane, percent] = state.pending;
        state.pending = null;
        state.active = target.invokeMethodAsync('InspectLifeline', lane, percent)
            .catch(error => { if (!state.retired) console.warn('Lifeline inspection unavailable.', error); })
            .finally(() => {
                state.active = null;
                if (!state.retired && state.pending) state.frame = requestAnimationFrame(flush);
            });
    };
    const inspect = (lane, percent = 50) => {
        state.lane = lane;
        state.percent = Math.max(0, Math.min(100, percent));
        state.pending = [lane, state.percent];
        if (!state.frame && !state.active) state.frame = requestAnimationFrame(flush);
    };
    const trackFor = event => event.target.closest('[data-lifeline-lane]');
    const point = event => {
        const track = trackFor(event);
        if (!track || !root.contains(track)) return;
        const box = track.getBoundingClientRect();
        if (box.width > 0) inspect(track.dataset.lifelineLane, (event.clientX - box.left) / box.width * 100);
    };
    root.addEventListener('pointermove', point, { signal: abort.signal });
    root.addEventListener('pointerdown', point, { signal: abort.signal });
    root.addEventListener('pointerout', event => {
        const track = trackFor(event);
        if (track && !track.contains(event.relatedTarget)) inspect(null);
    }, { signal: abort.signal });
    root.addEventListener('focusout', event => { if (trackFor(event)) inspect(null); }, { signal: abort.signal });
    root.addEventListener('keydown', event => {
        const track = trackFor(event);
        if (!track || !['ArrowLeft', 'ArrowRight', 'Home', 'End', 'Escape'].includes(event.key)) return;
        event.preventDefault();
        if (event.key === 'Escape') { inspect(null); return; }
        const current = state.lane === track.dataset.lifelineLane ? state.percent : 50;
        const step = event.shiftKey ? 10 : 2;
        inspect(track.dataset.lifelineLane, event.key === 'Home' ? 0 : event.key === 'End' ? 100 : current + (event.key === 'ArrowRight' ? step : -step));
    }, { signal: abort.signal });
}

export async function dispose(root) {
    const state = observers.get(root);
    if (!state) return;
    state.retired = true;
    state.abort.abort();
    cancelAnimationFrame(state.frame);
    cancelAnimationFrame(state.labelFrame);
    state.resize.disconnect();
    state.mutations.disconnect();
    state.pending = null;
    await state.active;
    observers.delete(root);
}
