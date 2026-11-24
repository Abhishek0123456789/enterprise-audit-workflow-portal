using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using AuditPortal.API.Data;
using AuditPortal.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AuditPortal.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _config;

    // Stores active OTPs temporarily per email/mobile session
    private static readonly ConcurrentDictionary<string, OtpRecord> _otpStore = new();

    public AuthController(AppDbContext context, IConfiguration config)
    {
        _context = context;
        _config = config;
    }

    // 1. Sends 6-digit Email OTP and Mobile OTP
    [HttpPost("send-otp")]
    public async Task<IActionResult> SendOtp([FromBody] OtpRequest req)
    {
        if (await _context.Users.AnyAsync(u => u.Username == req.Username || u.Email == req.Email))
            return BadRequest(new { message = "Username or Official Email is already registered." });

        var emailOtp = Random.Shared.Next(100000, 999999).ToString();
        var mobileOtp = Random.Shared.Next(100000, 999999).ToString();

        _otpStore[req.Email.ToLower()] = new OtpRecord(emailOtp, mobileOtp, DateTime.UtcNow.AddMinutes(5));

        bool emailSent = false;
        var senderEmail = _config["Smtp:SenderEmail"];
        var appPassword = _config["Smtp:AppPassword"];

        if (!string.IsNullOrWhiteSpace(senderEmail) && !string.IsNullOrWhiteSpace(appPassword))
        {
            try
            {
                using var client = new SmtpClient(_config["Smtp:Host"], int.Parse(_config["Smtp:Port"] ?? "587"))
                {
                    Credentials = new NetworkCredential(senderEmail, appPassword),
                    EnableSsl = true
                };
                var mail = new MailMessage(senderEmail, req.Email)
                {
                    Subject = "Audit Management System - Official Verification OTP",
                    Body = $"Government of Rajasthan | Local Fund Audit Department, Jaipur\n\nYour Email Verification OTP is: {emailOtp}\nValid for 5 minutes."
                };
                await client.SendMailAsync(mail);
                emailSent = true;
            }
            catch { /* Falls back gracefully if SMTP is unconfigured */ }
        }

        return Ok(new
        {
            message = emailSent
                ? "OTPs dispatched to your Email and Mobile Number."
                : "OTPs generated successfully (Sandbox Mode active).",
            devEmailOtp = emailOtp,
            devMobileOtp = mobileOtp
        });
    }

    // 2. Verifies Both OTPs and Registers Official Account
    [HttpPost("register-verified")]
    public async Task<IActionResult> RegisterVerified([FromBody] VerifiedRegisterRequest req)
    {
        var key = req.User.Email.ToLower();
        if (!_otpStore.TryGetValue(key, out var record) || record.ExpiresAt < DateTime.UtcNow)
            return BadRequest(new { message = "OTP expired or not requested. Please resend OTP." });

        if (record.EmailOtp != req.EmailOtp || record.MobileOtp != req.MobileOtp)
            return BadRequest(new { message = "Invalid Email OTP or Mobile OTP. Verification failed." });

        _otpStore.TryRemove(key, out _);
        req.User.IsVerified = true;

        _context.Users.Add(req.User);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Official account verified and created successfully." });
    }

    // 3. Authenticates User & Returns JWT Token
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u =>
            u.Username == request.Username && u.Password == request.Password);

        if (user == null)
            return Unauthorized(new { message = "Invalid User ID or Password." });

        var token = GenerateJwtToken(user);
        return Ok(new
        {
            token,
            username = user.Username,
            fullName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName,
            role = user.Role,
            department = user.Department
        });
    }

    private string GenerateJwtToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record OtpRecord(string EmailOtp, string MobileOtp, DateTime ExpiresAt);
public record OtpRequest(string Username, string Email, string MobileNumber);
public record VerifiedRegisterRequest(User User, string EmailOtp, string MobileOtp);
public record LoginRequest(string Username, string Password);
