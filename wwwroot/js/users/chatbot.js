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

        const staticReplies = {
            greeting:
                "Hi. I can help with hours, reservations, borrowing, fines, profile, and policies.",
            hours: "Library hours: Monday-Friday 8:00 AM-8:00 PM, Saturday-Sunday 9:00 AM-5:00 PM.",
            borrow: "Borrowing limit is up to 3 books at a time. The default borrowing duration is 14 days.",
            reserve:
                "To reserve a book: open a book detail page, add it to cart, then go to /cart and click Proceed Request.",
            fine: "Late return fee is $1.00 per late day. You can check overdue and fine details in /history.",
            history:
                "Open /history to see borrowed, overdue, and returned books with fine payment status.",
            
            account:
                "Use /login to sign in. After login, you can manage account details from /profile.",
            profile:
                "Use /profile to update your profile and /bookmark to manage favorite books.",
            review: "You can submit a 1-5 star review from each book detail page using the Feedback button.",
            policy: "Library policy is available at /about/policies.",
            search: "Use /book to browse or search by title, author, and category. Category filters are available on the Book page.",
            contact:
                "Ask at the circulation desk, or see /about/support for help.",
            location:
                "Library location: the SMA campus library building.",
            // The full walkthrough is built in addResetPasswordGuideMessage.
            resetPassword:
                "Open /Identity/Account/ForgotPassword and we will email you a reset link. If you cannot get in, staff at the circulation desk can reset it for you.",
        };

        const intentKeywords = [
            {
                intent: "greeting",
                keywords: [
                    "hello",
                    "hi",
                    "hey",
                    "good morning",
                    "good afternoon",
                ],
            },
            {
                intent: "hours",
                keywords: ["hour", "opening", "open time", "close time"],
            },
            {
                intent: "reserve",
                keywords: [
                    "reserve",
                    "reservation",
                    "request book",
                    "proceed request",
                    "cart request",
                ],
            },
            {
                intent: "borrow",
                keywords: [
                    "borrow limit",
                    "borrowing limit",
                    "how many books",
                    "borrow period",
                    "duration",
                ],
            },
            {
                intent: "fine",
                keywords: [
                    "fine",
                    "late fee",
                    "late return",
                    "penalty",
                    "overdue fee",
                ],
            },
            {
                intent: "history",
                keywords: [
                    "history",
                    "borrow history",
                    "overdue",
                    "returned books",
                ],
            },
            {
                intent: "resetPassword",
                keywords: [
                    "reset password",
                    "forgot password",
                    "password reset",
                    "telegram otp",
                    "otp code",
                    "send otp",
                    "/start",
                ],
            },
            {
                intent: "account",
                keywords: ["account", "login", "sign in", "register"],
            },
            {
                intent: "profile",
                keywords: [
                    "profile",
                    "bookmark",
                    "favorite",
                    "avatar",
                    "cover image",
                ],
            },
            {
                intent: "review",
                keywords: ["review", "rating", "star", "book feedback"],
            },
            {
                intent: "policy",
                keywords: ["policy", "rules", "terms", "copyright"],
            },
            {
                intent: "contact",
                keywords: ["contact", "support", "help", "feedback message"],
            },
            {
                intent: "location",
                keywords: ["where", "location", "address", "campus"],
            },
            {
                intent: "search",
                keywords: [
                    "search",
                    "find book",
                    "book list",
                    "category",
                    "browse",
                ],
            },
        ];

        const fallbackReply =
            "I can look up books, your loans, your fines and your reservations, and answer questions about hours, borrowing limits and library policies. Try asking: Where is Clean Code?";

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

        const sendBotReplyByIntent = (intent) => {
            if (intent === "resetPassword") {
                addResetPasswordGuideMessage();
                return;
            }

            addMessage(staticReplies[intent] || fallbackReply, "bot");
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

        const getIntent = (text) => {
            const normalized = (text || "").trim().toLowerCase();
            if (!normalized) {
                return "";
            }

            for (const entry of intentKeywords) {
                const isMatch = entry.keywords.some((keyword) =>
                    normalized.includes(keyword),
                );
                if (isMatch) {
                    return entry.intent;
                }
            }

            return "";
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
                // Never surface the technical failure - fall back to the canned reply.
                console.warn("Library assistant unavailable:", error);
                const intent = getIntent(question);
                if (intent) {
                    sendBotReplyByIntent(intent);
                } else {
                    addMessage(
                        "I could not reach the catalogue just now. Please try again, " +
                        "or ask at the circulation desk.",
                        "bot",
                    );
                }
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
            if (getIntent(cleaned) === "resetPassword") {
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

            const ask = target.dataset.ask || "";
            const intent = target.dataset.intent || "";
            const label = target.textContent
                ? target.textContent.trim()
                : "Question";

            if (ask) {
                addMessage(ask, "user");
                askAssistant(ask);
                return;
            }

            addMessage(label, "user");
            sendBotReplyByIntent(intent);
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
