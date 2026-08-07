(() => {
    const progress = document.getElementById('remi-restore-progress');
    if (!progress) return;

    const progressTitle = progress.querySelector('[data-remi-restore-progress-title]');
    const progressDescription = progress.querySelector('[data-remi-restore-progress-description]');
    const progressWarning = progress.querySelector('[data-remi-restore-progress-warning]');
    const progressEyebrow = progress.querySelector('[data-remi-restore-progress-eyebrow]');
    const progressReturn = progress.querySelector('[data-remi-restore-progress-return]');

    const restoreResultUrl = form => {
        const action = new URL(form.action);
        const result = new URL('/settings', window.location.origin);
        result.searchParams.set('section', 'data-transfer');
        result.searchParams.set('restore', 'token-failure');
        const period = action.searchParams.get('period');
        if (period) result.searchParams.set('period', period);
        return result.toString();
    };

    const showRestoreFailure = (form, message) => {
        progress.classList.add('restore-progress--failed');
        if (progressEyebrow) progressEyebrow.textContent = 'Restore could not start';
        if (progressTitle) progressTitle.textContent = 'Remi could not start the restore.';
        if (progressDescription) progressDescription.textContent = message;
        if (progressWarning) progressWarning.textContent = 'Your current data has not been replaced.';
        if (progressReturn instanceof HTMLAnchorElement) {
            progressReturn.href = restoreResultUrl(form);
            progressReturn.hidden = false;
        }
    };

    const submitRestore = async form => {
        try {
            const tokenResponse = await fetch('/data-transfer/restore/token', {
                cache: 'no-store',
                credentials: 'same-origin',
            });
            const { requestToken } = await tokenResponse.json();
            if (!tokenResponse.ok || typeof requestToken !== 'string') {
                throw new Error('A one-time restore token was not available.');
            }

            const response = await fetch(form.action, {
                body: new FormData(form),
                cache: 'no-store',
                credentials: 'same-origin',
                headers: { 'X-Remi-Restore-Token': requestToken },
                method: 'POST',
            });
            if (!response.ok) {
                throw new Error('The restore endpoint did not return a completion page.');
            }

            window.location.replace(response.url);
        } catch {
            showRestoreFailure(form, 'Remi could not establish the secure restore request. Reload Settings and try again.');
        }
    };

    const beginRestore = form => {
        if (!form.reportValidity()) return;
        if (form.dataset.remiRestoreSubmitting === 'true') return;

        form.dataset.remiRestoreSubmitting = 'true';
        progress.hidden = false;
        document.body.classList.add('restore-in-progress');
        const submit = form.querySelector('[data-remi-restore-submit]');
        if (submit instanceof HTMLButtonElement) {
            submit.disabled = true;
            submit.textContent = 'Restoring data...';
        }

        void submitRestore(form);
    };

    document.addEventListener('click', event => {
        const submit = event.target.closest('[data-remi-restore-submit]');
        if (!(submit instanceof HTMLButtonElement)) return;

        const form = submit.closest('form[data-remi-restore-form]');
        if (form instanceof HTMLFormElement) beginRestore(form);
    });

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-remi-restore-form')) return;
        event.preventDefault();
        beginRestore(form);
    });
})();
