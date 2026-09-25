using System.Net;
using System.Net.Mail;
using System.Text;
using ConexyAI.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service.Email;

/// <summary>
/// EMAIL_VERIFICATION: Gmail SMTP over STARTTLS.
///
/// <para>
/// Uses <see cref="SmtpClient"/> from the framework rather than MailKit: it is one class, needs no
/// new dependency (nothing to restore in the image build), and the only thing asked of it here is a
/// login, a STARTTLS upgrade and one HTML message. MailKit remains the better library for anything
/// richer — if this ever grows attachments, batching or delivery webhooks, replacing this file is
/// the whole change, because everything else depends on <see cref="IEmailSender"/>.
/// </para>
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendVerificationCodeAsync(string toEmail, string code, int validMinutes, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            throw new EmailSendFailedException("SMTP is not configured (SMTP_PASSWORD missing).");
        }

        var from = string.IsNullOrWhiteSpace(_options.FromEmail) ? _options.User : _options.FromEmail;

        using var message = new MailMessage
        {
            From = new MailAddress(from, _options.FromName, Encoding.UTF8),
            Subject = $"ConexyAI: код подтверждения {code}",
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true,
            Body = BuildHtml(code, validMinutes),
        };
        message.To.Add(new MailAddress(toEmail));
        // A plain-text alternative keeps the message readable in clients that refuse HTML. The code
        // is the only thing that matters here, so the text version says exactly that.
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            BuildText(code, validMinutes), Encoding.UTF8, "text/plain"));

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_options.User, _options.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20_000,
        };

        try
        {
            await client.SendMailAsync(message, ct);
            // PRIVACY_LOGS: the address is not logged, and neither is the code — only that a message
            // went out, so a stuck sign-up can be told apart from a silent transport failure.
            _logger.LogInformation("Email verification: code sent via {Host}:{Port}.", _options.Host, _options.Port);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Email verification: SMTP send failed via {Host}:{Port}.", _options.Host, _options.Port);
            throw new EmailSendFailedException("SMTP send failed.", ex);
        }
    }

    private static string BuildText(string code, int validMinutes) =>
        $"""
         ConexyAI — подтверждение почты

         Ваш код: {code}

         Код действует {validMinutes} минут.
         Если вы не регистрировались в ConexyAI, просто проигнорируйте это письмо: без кода доступ
         к аккаунту не появится. Никому не сообщайте код — сотрудники ConexyAI его не спрашивают.
         """;

    /// <summary>
    /// Minimal transactional layout: dark card, the code as the one large element, the expiry and the
    /// privacy note under it. Inline styles only — mail clients strip stylesheets.
    /// </summary>
    private static string BuildHtml(string code, int validMinutes) =>
        $"""
         <!doctype html>
         <html lang="ru">
           <body style="margin:0;padding:24px;background:#0a0a0a;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
             <div style="max-width:420px;margin:0 auto;background:#141414;border:1px solid #262626;border-radius:14px;padding:28px 24px;color:#ece7ee;">
               <div style="font-size:15px;font-weight:600;letter-spacing:0.02em;margin-bottom:6px;">ConexyAI</div>
               <div style="font-size:13px;color:#a3a3a3;margin-bottom:22px;">Подтверждение почты</div>

               <div style="font-size:13px;color:#d4d4d4;line-height:1.5;margin-bottom:14px;">
                 Введите этот код, чтобы завершить регистрацию:
               </div>

               <div style="font-size:34px;font-weight:700;letter-spacing:10px;text-align:center;padding:18px 0 18px 10px;background:#0a0a0a;border:1px solid #262626;border-radius:12px;color:#ffffff;">
                 {WebUtility.HtmlEncode(code)}
               </div>

               <div style="font-size:12.5px;color:#a3a3a3;line-height:1.5;margin-top:18px;">
                 Код действует {validMinutes} минут и подходит только для одного входа.
               </div>
               <div style="font-size:12.5px;color:#a3a3a3;line-height:1.5;margin-top:10px;">
                 Если вы не регистрировались в ConexyAI — просто проигнорируйте письмо: без кода доступ
                 к аккаунту не появится. Никому не сообщайте код, сотрудники ConexyAI его не спрашивают.
               </div>
             </div>
           </body>
         </html>
         """;
}
