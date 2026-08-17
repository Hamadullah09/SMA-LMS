/* ============================================================================
   Self-service kiosk client.

   The browser holds no state. It polls the server, renders whatever came back,
   and posts button presses — because the real state lives at the physical
   station: the books on the antenna and the card that was tapped against it.
   A reload, a crash or a second tab therefore cannot disagree with the pad.

   No framework and no build step on purpose. This has to keep working on a
   locked-down station with no internet, years after anyone last looked at it.
   ============================================================================ */
(function () {
    'use strict';

    var READER_ID = (window.smaKiosk && window.smaKiosk.readerId) || 0;

    /* Enums are serialised as numbers, so the names live here rather than
       being repeated as magic values through the rendering code. */
    var Stage = { Idle: 0, WaitingForCard: 1, Collecting: 2, Finished: 3, Unavailable: 4 };
    var Mode = { Borrow: 0, Return: 1 };

    /* Fast enough that a book landing on the pad feels instant, slow enough
       that an idle station is not hammering the database all day. */
    var POLL_MS = 900;

    /* After a failed poll, back off rather than retrying at full speed into a
       server that is restarting. */
    var POLL_BACKOFF_MS = 4000;

    var el = {};
    var lastVersion = -1;
    var renderFailures = 0;
    var inFlight = 0;
    var queue = Promise.resolve();
    var polling = null;

    // ---------------------------------------------------------------- helpers

    function $(id) { return document.getElementById(id); }

    /* Everything rendered here can contain a book title from the database, so
       nothing is ever assembled as an HTML string. */
    function text(tag, className, value) {
        var node = document.createElement(tag);
        if (className) { node.className = className; }
        if (value !== undefined && value !== null) { node.textContent = value; }
        return node;
    }

    function clear(node) {
        while (node.firstChild) { node.removeChild(node.firstChild); }
    }

    function formatDate(iso) {
        if (!iso) { return ''; }
        var d = new Date(iso);
        if (isNaN(d.getTime())) { return ''; }
        return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    }

    function money(currency, amount) {
        return currency + ' ' + Number(amount || 0).toFixed(2);
    }

    // ---------------------------------------------------------------- transport

    /* Button presses are SERIALISED, never dropped.
       An earlier version skipped a press while another request was in flight, which meant tapping
       "Return books" straight after "Done" could vanish with no feedback at all — the student sees
       a station that ignored them. Queueing keeps every press, in the order it was made. */
    function send(path, body) {
        queue = queue.then(function () { return post(path, body); });
        return queue;
    }

    function post(path, body) {
        inFlight++;

        var options = { method: 'POST', headers: {} };

        if (body) {
            options.headers['Content-Type'] = 'application/x-www-form-urlencoded';
            options.body = body;
        }

        return fetch('/kiosk/' + path + '/' + READER_ID, options)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (state) {
                if (state) { render(state, true); }
                return state;
            })
            .catch(function () { return null; })
            .finally(function () { inFlight--; });
    }

    function poll() {
        // A queued action is about to render newer state; polling over the top of it would show
        // the student the state from before their press.
        if (inFlight > 0) { return; }

        fetch('/kiosk/state/' + READER_ID, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (state) {
                if (state) { render(state, false); }
            })
            .catch(function () {
                /* The station is more useful saying it lost the server than
                   silently showing stale data somebody might act on. */
                setReaderState(false, 'No connection to the library system');
                schedule(POLL_BACKOFF_MS);
            });
    }

    function schedule(delay) {
        if (polling) { clearTimeout(polling); }
        polling = setTimeout(function () {
            poll();
            schedule(POLL_MS);
        }, delay);
    }

    // ---------------------------------------------------------------- rendering

    function setReaderState(online, label) {
        el.readerState.className = 'kiosk-link ' + (online ? 'is-online' : 'is-offline');
        el.readerState.textContent = (online ? '● ' : '○ ') + label;
    }

    function render(state, force) {
        /* Re-rendering steals focus and restarts the card animation, so an
           unchanged poll — which is most of them — must do nothing. */
        if (!force && state.version === lastVersion) {
            renderTimer(state);
            return;
        }

        /* The version is recorded only once the DOM actually reflects it, and any failure resets
           the cache so the next poll retries.

           Getting this wrong is unusually punishing on a kiosk: marking the version as rendered
           first means a single mid-render exception leaves a half-drawn screen that every later
           poll then skips as "already current". The station appears frozen with stale books on it
           until somebody reloads the page by hand, which nobody is there to do. */
        try {
            el.stationName.textContent = state.readerName || '';
            setReaderState(state.readerOnline,
                state.readerOnline ? 'Reader ready' : 'Reader offline — please use the desk');

            renderSteps(state);
            renderModes(state);
            renderWho(state);
            renderNotices(state);
            renderBasket(state);
            renderActions(state);
            renderReceipt(state);
            renderHint(state);
            renderTimer(state);

            lastVersion = state.version;
            renderFailures = 0;
        } catch (e) {
            lastVersion = -1;
            renderFailures++;

            if (window.console) {
                console.error('kiosk render failed (' + renderFailures + ')', e);
            }

            /* Last resort for an unattended station. If drawing the screen keeps failing there is
               nobody standing there to press reload, and a kiosk showing a stale or half-drawn
               screen is worse than one that blinks and comes back correct. Reloading re-runs
               everything from a clean slate; the station's real state lives on the server, so
               nothing is lost. Bounded by the counter so this can never become a reload loop. */
            if (renderFailures >= 3) {
                renderFailures = 0;
                if (window.console) { console.error('kiosk giving up and reloading'); }
                location.reload();
            }
        }
    }

    /* Numbered progress strip. Which step is current depends on the mode: returning
       needs no card, so step 1 is already satisfied and the student starts at "add
       your books". */
    function renderSteps(state) {
        var returning = state.mode === Mode.Return;
        var hasStudent = !!state.studentName;
        var hasBooks = (state.items || []).length > 0;

        var current =
            state.stage === Stage.Finished ? 4
            : returning ? (hasBooks ? 3 : 2)
            : !hasStudent ? 1
            : hasBooks ? 3 : 2;

        el.step1Label.textContent = returning ? 'No card needed' : 'Tap your card';

        [1, 2, 3].forEach(function (n) {
            var li = el.steps.querySelector('[data-step="' + n + '"]');
            if (!li) { return; }
            // Returning satisfies step 1 from the outset rather than leaving it pending.
            var done = n < current || (returning && n === 1);
            li.classList.toggle('is-done', done);
            li.classList.toggle('is-current', n === current && !done);
        });
    }

    function renderModes(state) {
        var borrowing = state.mode === Mode.Borrow;

        el.modeBorrow.classList.toggle('is-active', borrowing);
        el.modeReturn.classList.toggle('is-active', !borrowing);
        el.modeBorrow.setAttribute('aria-selected', String(borrowing));
        el.modeReturn.setAttribute('aria-selected', String(!borrowing));
    }

    function renderWho(state) {
        clear(el.whoBody);

        if (state.studentName) {
            el.whoBody.appendChild(text('div', 'kiosk-who__name', state.studentName));
            el.whoBody.appendChild(text('div', 'kiosk-who__meta', 'Roll No: ' + (state.rollNumber || '—')));

            if (state.department) {
                el.whoBody.appendChild(text('div', 'kiosk-who__meta', state.department));
            }

            (state.studentWarnings || []).forEach(function (w) {
                el.whoBody.appendChild(text('div', 'kiosk-notice kiosk-notice--danger', '✕ ' + w));
            });

            return;
        }

        var prompt = text('div', 'kiosk-who__prompt');

        if (state.mode === Mode.Return) {
            prompt.appendChild(text('div', 'kiosk-card-glyph', '📚'));
            prompt.appendChild(text('div', 'kiosk-empty__title', 'No card needed'));
            prompt.appendChild(text('p', null, 'Just place the books you are returning on the pad.'));
        } else {
            prompt.appendChild(text('div', 'kiosk-card-glyph', '🪪'));
            prompt.appendChild(text('div', 'kiosk-empty__title', 'Tap your student card'));
            prompt.appendChild(text('p', null, 'Hold it flat against the pad until your name appears.'));
        }

        el.whoBody.appendChild(prompt);
    }

    function renderNotices(state) {
        clear(el.notices);

        (state.notices || []).forEach(function (n) {
            el.notices.appendChild(text('div', 'kiosk-notice', '▲ ' + n));
        });
    }

    function renderBasket(state) {
        clear(el.basket);

        var items = state.items || [];
        el.padCount.textContent = items.length ? '(' + items.length + ')' : '';

        if (!items.length) {
            var empty = text('li', 'kiosk-empty');
            empty.appendChild(text('div', 'kiosk-empty__glyph', '📖'));
            empty.appendChild(text('div', 'kiosk-empty__title', 'Place your books on the pad'));
            empty.appendChild(text('p', null, state.mode === Mode.Borrow
                ? 'They will appear here as they are detected. You can add several at once.'
                : 'Each book you are returning will appear here with anything owed on it.'));
            el.basket.appendChild(empty);
            return;
        }

        items.forEach(function (item) {
            el.basket.appendChild(renderItem(state, item));
        });
    }

    function renderItem(state, item) {
        var li = text('li', 'kiosk-item ' + (item.allowed ? 'is-ok' : 'is-blocked'));

        /* Cover first. A student holding a book recognises the artwork before they
           read the title, which is the fastest way to confirm the pad read the
           right item. Falls back to a placeholder so the row never reflows. */
        if (item.coverUrl) {
            var img = document.createElement('img');
            img.className = 'kiosk-item__cover';
            img.src = item.coverUrl;
            img.alt = '';
            img.setAttribute('aria-hidden', 'true');
            // A broken path must not leave a torn-image icon on a public screen.
            img.addEventListener('error', function () {
                img.replaceWith(text('div', 'kiosk-item__cover kiosk-item__cover--none', '📕'));
            });
            li.appendChild(img);
        } else {
            li.appendChild(text('div', 'kiosk-item__cover kiosk-item__cover--none', '📕'));
        }

        li.appendChild(text('div', 'kiosk-item__glyph', item.allowed ? '✓' : '✕'));

        var body = text('div');
        body.appendChild(text('div', 'kiosk-item__title', item.title));

        var meta = [];
        if (item.author) { meta.push(item.author); }
        if (item.copyNumber) { meta.push('Copy ' + item.copyNumber); }
        if (item.accessionNumber) { meta.push(item.accessionNumber); }
        if (meta.length) { body.appendChild(text('div', 'kiosk-item__meta', meta.join(' · '))); }

        if (state.mode === Mode.Return && item.dueUtc) {
            body.appendChild(text('div', 'kiosk-item__meta', 'Was due ' + formatDate(item.dueUtc)));
        }

        if (item.message) {
            body.appendChild(text('div', 'kiosk-item__message', item.message));
        }

        li.appendChild(body);

        /* Lets a student put back a book the limit refused without abandoning
           the whole basket and starting again. */
        var remove = text('button', 'kiosk-item__remove', '✕');
        remove.type = 'button';
        remove.setAttribute('aria-label', 'Remove ' + item.title);
        remove.addEventListener('click', function () {
            send('remove', 'bookCopyId=' + encodeURIComponent(item.bookCopyId));
        });
        li.appendChild(remove);

        return li;
    }

    function renderActions(state) {
        var count = state.allowedCount || 0;

        el.commitButton.disabled = !state.canCommit;
        el.commitButton.textContent = count === 0
            ? 'Confirm'
            : state.mode === Mode.Borrow
                ? (count === 1 ? 'Borrow 1 book' : 'Borrow ' + count + ' books')
                : (count === 1 ? 'Return 1 book' : 'Return ' + count + ' books');
    }

    function renderHint(state) {
        var hint;

        switch (state.stage) {
            case Stage.Unavailable:
                hint = 'This station is unavailable. Please use the circulation desk.';
                break;
            case Stage.WaitingForCard:
                hint = 'Books detected — now tap your student card to borrow them.';
                break;
            case Stage.Collecting:
                hint = state.mode === Mode.Borrow
                    ? 'Add more books, or press Borrow when you are done. Loans are ' + state.loanDays + ' days.'
                    : 'Add more books, or press Return when you are done.';
                break;
            case Stage.Finished:
                hint = 'Take your books and your receipt.';
                break;
            default:
                hint = state.mode === Mode.Borrow
                    ? 'Place your books on the pad and tap your student card.'
                    : 'Place the books you are returning on the pad.';
        }

        el.hint.textContent = hint;
    }

    function renderTimer(state) {
        var idle = state.stage === Stage.Idle || state.stage === Stage.Unavailable;

        if (idle) {
            el.timer.textContent = '';
            el.timer.classList.remove('is-urgent');
            return;
        }

        var seconds = state.idleSecondsRemaining;
        el.timer.textContent = 'Clears in ' + seconds + 's';
        el.timer.classList.toggle('is-urgent', seconds <= 15);
    }

    // ---------------------------------------------------------------- receipt

    function renderReceipt(state) {
        var receipt = state.receipt;

        if (!receipt) {
            el.receipt.hidden = true;
            return;
        }

        /* Content is built while the overlay is still hidden, and the overlay is revealed at the
           very end (see the bottom of this function).

           Revealing first is what produced a full-screen blank receipt on a live station: anything
           that threw afterwards left the panel visible with no headline, no name and no lines, and
           because it threw on every subsequent poll too, the kiosk sat on that empty screen until
           somebody reloaded it. Nothing is shown until there is something to show. */

        var failed = receipt.failedCount || 0;
        var ok = receipt.succeededCount || 0;
        var tone = failed === 0 ? 'is-ok' : (ok === 0 ? 'is-fail' : 'is-partial');

        el.receiptHeadline.className = 'kiosk-receipt__headline ' + tone;
        el.receiptHeadline.textContent = (failed === 0 ? '✓ ' : ok === 0 ? '✕ ' : '▲ ') + receipt.headline;

        var who = receipt.studentName || 'Self-service';
        if (receipt.rollNumber && receipt.rollNumber !== '—') { who += ' · ' + receipt.rollNumber; }

        var when = receipt.completedUtc ? new Date(receipt.completedUtc) : null;
        el.receiptWho.textContent = when && !isNaN(when.getTime())
            ? who + ' · ' + when.toLocaleString()
            : who;

        clear(el.receiptLines);

        (receipt.lines || []).forEach(function (line) {
            var li = text('li', 'kiosk-receipt__line' + (line.succeeded ? '' : ' is-fail'));

            li.appendChild(text('div', null, line.succeeded ? '✓' : '✕'));

            var body = text('div');
            body.appendChild(text('div', 'kiosk-item__title', line.title));
            body.appendChild(text('div', 'kiosk-item__meta',
                (line.copyNumber ? 'Copy ' + line.copyNumber + ' · ' : '') + line.message));

            if (line.transactionNumber) {
                body.appendChild(text('div', 'kiosk-item__meta', line.transactionNumber));
            }

            li.appendChild(body);

            if (line.dueUtc) {
                li.appendChild(text('div', 'kiosk-receipt__due', 'Due ' + formatDate(line.dueUtc)));
            } else if (line.fine > 0) {
                li.appendChild(text('div', 'kiosk-receipt__due', money(receipt.currency, line.fine)));
            }

            el.receiptLines.appendChild(li);
        });

        el.receiptTotal.textContent = receipt.totalFine > 0
            ? 'To pay at the desk: ' + money(receipt.currency, receipt.totalFine)
            : '';

        renderPrintDocument(receipt);

        // Everything above succeeded, so there is now something worth covering the screen with.
        el.receipt.hidden = false;
    }

    /* Fills the print-only document.
       Built here rather than server-side so printing needs no round trip — the student presses
       Print and the paper is already composed. */
    function renderPrintDocument(receipt) {
        var borrowing = receipt.mode === Mode.Borrow;
        var when = receipt.completedUtc ? new Date(receipt.completedUtc) : null;
        var validWhen = when && !isNaN(when.getTime());

        el.docKind.textContent = borrowing
            ? 'LOAN RECEIPT'
            : (receipt.totalFine > 0 ? 'RETURN RECEIPT & FINE NOTICE' : 'RETURN RECEIPT');

        el.docNumber.textContent = receipt.documentNumber || '—';
        el.docIssued.textContent = validWhen ? when.toLocaleString() : '—';
        el.docStation.textContent = receipt.stationName || 'Self-service station';

        el.docName.textContent = receipt.studentName || '—';
        el.docRoll.textContent = receipt.rollNumber && receipt.rollNumber !== '—'
            ? 'Roll number: ' + receipt.rollNumber : '';
        el.docDept.textContent = receipt.department || '';

        // Returns show what was charged; loans show when the book is due back.
        el.docLastCol.textContent = borrowing ? 'Due date' : 'Fine';

        clear(el.docItems);

        var n = 0;
        (receipt.lines || []).forEach(function (line) {
            n++;
            var tr = document.createElement('tr');
            if (!line.succeeded) { tr.className = 'kiosk-doc__failed'; }

            var cells = [
                { text: String(n) },
                { text: line.title, cls: 'kiosk-doc__titleCell' },
                { text: line.author || '' },
                {
                    // Both, because the desk searches by accession number but shelves are
                    // organised by copy.
                    text: [line.copyNumber ? 'Copy ' + line.copyNumber : null, line.accessionNumber]
                        .filter(Boolean).join(' · ')
                },
                { text: line.transactionNumber || '—', cls: 'kiosk-doc__mono' },
                {
                    text: borrowing
                        ? (line.dueUtc ? formatDate(line.dueUtc) : '—')
                        : (line.fine > 0 ? money(receipt.currency, line.fine) : 'None'),
                    cls: 'kiosk-doc__right'
                }
            ];

            cells.forEach(function (c) {
                var td = document.createElement('td');
                td.textContent = c.text;
                if (c.cls) { td.className = c.cls; }
                tr.appendChild(td);
            });

            el.docItems.appendChild(tr);
        });

        // ---- summary ----
        clear(el.docSummary);

        var summaryRow = function (label, value) {
            var row = text('div', 'kiosk-doc__summaryRow');
            row.appendChild(text('span', null, label));
            row.appendChild(text('strong', null, value));
            el.docSummary.appendChild(row);
        };

        summaryRow(borrowing ? 'Books issued' : 'Books returned', String(receipt.succeededCount || 0));

        if (receipt.failedCount > 0) {
            summaryRow('Not completed', String(receipt.failedCount));
        }

        if (receipt.totalFine > 0) {
            var due = text('div', 'kiosk-doc__due',
                'Amount payable: ' + money(receipt.currency, receipt.totalFine));
            el.docSummary.appendChild(due);
        }

        // ---- terms ----
        clear(el.docTerms);

        var terms = [];

        if (borrowing) {
            terms.push('Loan period is ' + (receipt.loanDays || 14) + ' days from the date of issue.');
            terms.push('Return each book on or before the due date shown above.');
            if (receipt.finePerDay > 0) {
                terms.push('Overdue books are charged at '
                    + money(receipt.currency, receipt.finePerDay) + ' per day, per book.');
            }
            terms.push('You remain responsible for each book until it is returned and scanned in.');
        } else {
            terms.push('Each book listed above has been scanned in and is no longer on your account.');
            if (receipt.totalFine > 0) {
                terms.push('The amount payable is settled at the circulation desk, not at this station.');
            } else {
                terms.push('No fines were charged on this return.');
            }
        }

        if (receipt.failedCount > 0) {
            terms.push('Items marked "not completed" were NOT processed. Take them to the '
                + 'circulation desk.');
        }

        terms.push('Retain this document as proof of the transaction.');

        terms.forEach(function (t) { el.docTerms.appendChild(text('li', null, t)); });

        el.docFootRef.textContent = receipt.documentNumber || '';
    }

    // ---------------------------------------------------------------- wiring

    function start() {
        ['stationName', 'readerState', 'steps', 'step1Label', 'modeBorrow', 'modeReturn',
            'whoBody', 'notices', 'basket', 'padCount', 'commitButton', 'resetButton',
            'hint', 'timer', 'receipt', 'receiptHeadline', 'receiptWho', 'receiptLines',
            'receiptTotal', 'doneButton', 'printButton',
            'printDoc', 'docKind', 'docNumber', 'docIssued', 'docStation', 'docName', 'docRoll',
            'docDept', 'docLastCol', 'docItems', 'docSummary', 'docTerms', 'docFootRef']
            .forEach(function (id) { el[id] = $(id); });

        el.modeBorrow.addEventListener('click', function () { send('mode', 'mode=' + Mode.Borrow); });
        el.modeReturn.addEventListener('click', function () { send('mode', 'mode=' + Mode.Return); });

        el.commitButton.addEventListener('click', function () {
            el.commitButton.disabled = true;
            send('commit');
        });

        el.resetButton.addEventListener('click', function () { send('reset'); });
        el.doneButton.addEventListener('click', function () { send('reset'); });
        el.printButton.addEventListener('click', function () { window.print(); });

        poll();
        schedule(POLL_MS);

        /* A station left on a background tab is throttled by the browser and
           would show a stale basket the moment somebody walks up to it. */
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) { poll(); }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
