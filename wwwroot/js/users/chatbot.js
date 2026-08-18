(() => {
    function setupStaticChatbot() {
        const chatbot = document.getElementById("homeChatbot");
        if (!chatbot || chatbot.dataset.initialized === "1") {
            return;
        }

        chatbot.dataset.initialized = "1";

        const toggleButton = chatbot.querySelector("#homeChatbotToggle");
        const panel = chatbot.querySelector("#homeChatbotPanel");
        const closeButton = chatbot.querySelector("#homeChatbotClose");
        const messages = chatbot.querySelector("#homeChatbotMessages");
        const quickActions = chatbot.querySelector("#homeChatbotQuickActions");
        const form = chatbot.querySelector("#homeChatbotForm");
        const input = chatbot.querySelector("#homeChatbotInput");

        if (
            !toggleButton ||
            !panel ||
            !closeButton ||
            !messages ||
            !quickActions ||
            !form ||
            !input
        ) {
            return;
        }

        // Everything factual comes from /assistant/ask, which reads the live catalogue,
        // the configured library policy and the signed-in student's own record.
        //
        // There used to be a staticReplies table here that answered from hard-coded
        // strings whenever a keyword matched, and it had drifted badly: it told
        // students the limit was 3 books over 14 days at "$1.00 per late day", when
        // the configured policy is 5 books and PKR 20.00 a day, and it quoted opening
        // hours that contradicted the ones printed on the home and About pages. A
        // confidently wrong answer about a fine is worse than no answer, so the table
        // is gone and the only local message left is the reset-password walkthrough,
        // which is a rich message with steps and a QR image rather than a fact.

        const isResetPasswordQuestion = (text) =>
            /\b(reset|forgot|change).{0,20}(password|pin)\b|\bpassword\b.{0,20}\b(reset|forgot)\b/i
                .test((text || "").trim());


        const appendMessageElement = (item) => {
            messages.appendChild(item);
            messages.scrollTop = messages.scrollHeight;
        };

        const addMessage = (text, type) => {
            const item = document.createElement("div");
            item.className = `home-chatbot__msg home-chatbot__msg--${type}`;
            item.textContent = text;
            appendMessageElement(item);
        };

        const addResetPasswordGuideMessage = () => {
            const item = document.createElement("div");
            item.className =
                "home-chatbot__msg home-chatbot__msg--bot home-chatbot__msg--guide";

            const title = document.createElement("p");
            title.className = "home-chatbot__guide-title";
            title.textContent = "How to reset your password:";
            item.appendChild(title);

            const steps = [
                "Open the Reset Password page.",
                "Enter the email address registered on your library account.",
                "Open the link we email you — check your spam folder if it has not arrived.",
                "Choose a new password. The link works once.",
            ];

            const list = document.createElement("ol");
            list.className = "home-chatbot__guide-list";
            steps.forEach((step) => {
                const li = document.createElement("li");
                li.textContent = step;
                list.appendChild(li);
            });
            item.appendChild(list);

            const links = document.createElement("div");
            links.className = "home-chatbot__guide-links";

            const openResetLink = document.createElement("a");
            openResetLink.href = "/Identity/Account/ForgotPassword";
            openResetLink.textContent = "Open Reset Password Page";
            links.appendChild(openResetLink);

            item.appendChild(links);

            appendMessageElement(item);
        };

        const openPanel = () => {
            chatbot.classList.add("is-open");
            panel.hidden = false;
            toggleButton.setAttribute("aria-expanded", "true");

            if (!chatbot.dataset.seeded) {
                addMessage(
                    "Hi. Ask me where a book is, whether we have it, or about your own loans and fines. " +
                    "I answer from the live catalogue, so if I say it is on the shelf, it is.",
                    "bot",
                );
                chatbot.dataset.seeded = "1";
            }
        };

        const closePanel = () => {
            chatbot.classList.remove("is-open");
            toggleButton.setAttribute("aria-expanded", "false");
            panel.hidden = true;
        };

        // ---- Live assistant -------------------------------------------------
        // Answers come from /assistant/ask, which reads the real catalogue and the
        // signed-in student's own record. The canned replies below are kept only as
        // a fallback for the few things the assistant has no data for (opening hours,
        // the Telegram OTP walkthrough) and for when the request fails.

        const addTypingIndicator = () => {
            const item = document.createElement("div");
            item.className = "home-chatbot__msg home-chatbot__msg--bot home-chatbot__msg--typing";
            item.setAttribute("aria-label", "Assistant is typing");
            item.innerHTML = "<span></span><span></span><span></span>";
            appendMessageElement(item);
            return item;
        };

        const addAssistantAnswer = (answer) => {
            const item = document.createElement("div");
            item.className = "home-chatbot__msg home-chatbot__msg--bot";

            // Answers can list several loans, one per line.
            const body = document.createElement("p");
            body.className = "home-chatbot__answer";
            body.textContent = answer.text;
            item.appendChild(body);

            if (Array.isArray(answer.links) && answer.links.length > 0) {
                const linkRow = document.createElement("div");
                linkRow.className = "home-chatbot__answer-links";
                answer.links.forEach((link) => {
                    const a = document.createElement("a");
                    a.href = link.url;
                    a.textContent = link.label;
                    linkRow.appendChild(a);
                });
                item.appendChild(linkRow);
            }

            appendMessageElement(item);
        };

        const askAssistant = async (question) => {
            const typing = addTypingIndicator();

            try {
                const response = await fetch(
                    "/assistant/ask?q=" + encodeURIComponent(question),
                    { headers: { Accept: "application/json" } },
                );

                if (!response.ok) {
                    throw new Error("assistant responded " + response.status);
                }

                const answer = await response.json();
                typing.remove();

                if (!answer || !answer.text) {
                    throw new Error("empty answer");
                }

                addAssistantAnswer(answer);
            } catch (error) {
                typing.remove();
                // Say we could not reach it, rather than inventing an answer. The old
                // path fell back to a keyword-matched canned string here, which is how
                // a student could be told the fine was "$1.00 per late day".
                console.warn("Library assistant unavailable:", error);
                addMessage(
                    "I could not reach the catalogue just now. Please try again, " +
                    "or ask at the circulation desk.",
                    "bot",
                );
            }
        };

        const sendUserMessage = (text) => {
            const cleaned = (text || "").trim();
            if (!cleaned) {
                return;
            }

            addMessage(cleaned, "user");

            // The Telegram OTP walkthrough is a rich message with steps and a QR image
            // that the assistant API does not produce, so it stays local.
            if (isResetPasswordQuestion(cleaned)) {
                addResetPasswordGuideMessage();
                return;
            }

            askAssistant(cleaned);
        };

        toggleButton.addEventListener("click", () => {
            if (chatbot.classList.contains("is-open")) {
                closePanel();
                return;
            }

            openPanel();
            input.focus();
        });

        closeButton.addEventListener("click", closePanel);

        quickActions.addEventListener("click", (event) => {
            const target = event.target;
            if (!(target instanceof HTMLButtonElement)) {
                return;
            }

            // Every quick action is a real question now, so they all take the same
            // route a typed question does.
            const ask = target.dataset.ask || "";
            if (!ask) {
                return;
            }

            sendUserMessage(ask);
        });

        form.addEventListener("submit", (event) => {
            event.preventDefault();
            const value = input.value;
            input.value = "";
            sendUserMessage(value);
            input.focus();
        });

        document.addEventListener("click", (event) => {
            if (!chatbot.classList.contains("is-open")) {
                return;
            }

            const target = event.target;
            if (target instanceof Node && chatbot.contains(target)) {
                return;
            }

            closePanel();
        });

        window.addEventListener("keydown", (event) => {
            if (event.key === "Escape") {
                closePanel();
            }
        });
    }

    document.addEventListener("DOMContentLoaded", setupStaticChatbot);
})();
