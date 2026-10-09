// CAS-021 §8.1: small, unobtrusive vanilla JS gating the Sign In button on both fields being
// non-empty (V1). No Blazor interactivity/SignalR circuit is used for this — Login.razor is
// rendered statically (see Login.razor's top-of-file comment for why).
(function () {
    function updateButtonState() {
        var username = document.getElementById('username');
        var password = document.getElementById('password');
        var button = document.getElementById('sign-in-btn');

        if (!username || !password || !button) {
            return;
        }

        button.disabled = username.value.trim().length === 0 || password.value.trim().length === 0;
    }

    document.addEventListener('DOMContentLoaded', function () {
        var username = document.getElementById('username');
        var password = document.getElementById('password');

        if (username) {
            username.addEventListener('input', updateButtonState);
        }
        if (password) {
            password.addEventListener('input', updateButtonState);
        }

        updateButtonState();
    });
})();
