// Σύντομο "ding" όταν στέλνεται επιτυχώς μια παραγγελία στην κουζίνα/μπαρ.
// Χρησιμοποιεί το Web Audio API, δεν χρειάζεται αρχείο ήχου.
window.playBeep = function () {
    try {
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        const ctx = new AudioCtx();

        function tone(freq, startTime, duration) {
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.connect(gain);
            gain.connect(ctx.destination);
            osc.frequency.value = freq;
            osc.type = 'sine';
            gain.gain.setValueAtTime(0.0001, startTime);
            gain.gain.exponentialRampToValueAtTime(0.25, startTime + 0.01);
            gain.gain.exponentialRampToValueAtTime(0.0001, startTime + duration);
            osc.start(startTime);
            osc.stop(startTime + duration);
        }

        const now = ctx.currentTime;
        tone(880, now, 0.12);
        tone(1175, now + 0.13, 0.15);
    } catch (e) {
        // Σιωπηλή αποτυχία (π.χ. παλιό browser) - δεν μπλοκάρει τίποτα
    }
};

// Ελεύθερο σύρσιμο τραπεζιών στην κάτοψη (admin). Χρησιμοποιεί pointer events +
// pointer capture (δουλεύει με ποντίκι ΚΑΙ αφή) αντί για το ασταθές HTML5 drag-and-drop.
window.initFloorplanDrag = function (canvasEl, dotNetRef) {
    if (!canvasEl || canvasEl.dataset.dragInit === '1') return;
    canvasEl.dataset.dragInit = '1';

    canvasEl.addEventListener('pointerdown', function (e) {
        if (e.target.closest('.floor-table-remove')) return;
        const draggedEl = e.target.closest('.floor-table');
        if (!draggedEl || !canvasEl.contains(draggedEl)) return;

        e.preventDefault();
        draggedEl.classList.add('dragging');
        draggedEl.setPointerCapture(e.pointerId);

        function onMove(ev) {
            const rect = canvasEl.getBoundingClientRect();
            let x = ev.clientX - rect.left;
            let y = ev.clientY - rect.top;
            x = Math.max(0, Math.min(rect.width, x));
            y = Math.max(0, Math.min(rect.height, y));

            const xPct = (x / rect.width) * 100;
            const yPct = (y / rect.height) * 100;

            draggedEl.style.left = xPct + '%';
            draggedEl.style.top = yPct + '%';
            draggedEl.dataset.pendingX = xPct;
            draggedEl.dataset.pendingY = yPct;
        }

        function onUp(ev) {
            try { draggedEl.releasePointerCapture(ev.pointerId); } catch (err) { }
            draggedEl.removeEventListener('pointermove', onMove);
            draggedEl.removeEventListener('pointerup', onUp);
            draggedEl.removeEventListener('pointercancel', onUp);
            draggedEl.classList.remove('dragging');

            const tableId = draggedEl.getAttribute('data-table-id');
            const xPct = draggedEl.dataset.pendingX;
            const yPct = draggedEl.dataset.pendingY;

            if (tableId && xPct !== undefined && yPct !== undefined) {
                dotNetRef.invokeMethodAsync('SavePosition', parseInt(tableId, 10), parseFloat(xPct), parseFloat(yPct));
            }
        }

        draggedEl.addEventListener('pointermove', onMove);
        draggedEl.addEventListener('pointerup', onUp);
        draggedEl.addEventListener('pointercancel', onUp);
    });

    // Εμποδίζει το click (μετά το σύρσιμο) να φτάσει στον χειριστή του Blazor
    // που τοποθετεί ΝΕΟ τραπέζι - αλλιώς μπορεί να "παλέψουν" μεταξύ τους.
    canvasEl.addEventListener('click', function (e) {
        if (e.target.closest('.floor-table')) {
            e.stopPropagation();
        }
    });
};


// Mobile browser recovery: when Safari/Chrome restores this page from the
// back-forward cache, the HTML may return while the Blazor Server circuit is dead.
// Reload only for a BFCache restoration, not on every tab switch, to avoid losing an order in progress.
(function () {
    window.addEventListener('pageshow', function (event) {
        if (event && event.persisted) {
            window.location.reload();
        }
    });
})();
