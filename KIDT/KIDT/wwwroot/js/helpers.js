// Theme-Helper: setzt data-theme Attribut auf html-Element
window.setTheme = function(theme) {
    document.documentElement.setAttribute('data-theme', theme);
};

// Textarea-Helper: Enter-Handler und Reset
window.textareaHelper = {
    clearAndReset: function(elementId) {
        const textarea = document.getElementById(elementId);
        if (textarea) {
            textarea.value = '';
            textarea.rows = 1;
            textarea.style.height = 'auto';
        }
    },

    setupEnterHandler: function(elementId, dotnetHelper) {
        const textarea = document.getElementById(elementId);
        if (textarea) {
            textarea.addEventListener('keydown', function(e) {
                if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault();
                    dotnetHelper.invokeMethodAsync('OnEnterPressed');
                }
            });
        }
    }
};

// Auto-Scroll Helper fuer Chat-Nachrichten
window.chatScrollHelper = {
    userHasScrolledUp: false,
    messagesElement: null,

    init: function(messagesElement) {
        this.messagesElement = messagesElement;
        this.userHasScrolledUp = false;

        messagesElement.addEventListener('scroll', () => {
            const isAtBottom = messagesElement.scrollHeight - messagesElement.scrollTop <= messagesElement.clientHeight + 50;
            this.userHasScrolledUp = !isAtBottom;
        });
    },

    scrollToBottom: function() {
        if (!this.userHasScrolledUp && this.messagesElement) {
            this.messagesElement.scrollTop = this.messagesElement.scrollHeight;
        }
    },

    forceScrollToBottom: function() {
        if (this.messagesElement) {
            this.messagesElement.scrollTop = this.messagesElement.scrollHeight;
            this.userHasScrolledUp = false;
        }
    }
};

// Clipboard-Helper fuer Dokument-Kopieren via Strg+C / Strg+V
window.clipboardHelper = {
    dotnetRef: null,

    register: function(dotnetRef) {
        this.dotnetRef = dotnetRef;
        document.addEventListener('keydown', function(e) {
            if (!window.clipboardHelper.dotnetRef) return;
            if (e.ctrlKey && e.key === 'c') {
                window.clipboardHelper.dotnetRef.invokeMethodAsync('OnCtrlC');
            }
            if (e.ctrlKey && e.key === 'v') {
                window.clipboardHelper.dotnetRef.invokeMethodAsync('OnCtrlV');
            }
        });
    },

    dispose: function() {
        this.dotnetRef = null;
    }
};
