namespace ConexyAI.Contract;

// CHANGE_PASSWORD: добавлено 2026-11-21 — смена пароля из настроек аккаунта. Текущий пароль
// подтверждает, что действие делает владелец; новый проверяется той же политикой, что при регистрации.
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
