﻿using System;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Bit.Core.Entities;
using Bit.Core.Enums;
using Bit.Core.Identity;
using Bit.Core.IdentityServer;
using Bit.Core.Models.Business.Tokenables;
using Bit.Core.Repositories;
using Bit.Core.Resources;
using Bit.Core.Services;
using Bit.Core.Settings;
using Bit.Core.Tokens;
using Bit.Core.Utilities;
using Bit.Infrastructure.Dapper;
using Bit.Infrastructure.EntityFramework;
using IdentityModel;
using IdentityServer4.AccessTokenValidation;
using IdentityServer4.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog.Context;
using NoopRepos = Bit.Core.Repositories.Noop;
using TableStorageRepos = Bit.Core.Repositories.TableStorage;

namespace Bit.SharedWeb.Utilities
{
    public static class ServiceCollectionExtensions
    {
        public static void AddSqlServerRepositories(this IServiceCollection services, GlobalSettings globalSettings)
        {
            var selectedDatabaseProvider = globalSettings.DatabaseProvider;
            var provider = SupportedDatabaseProviders.SqlServer;
            var connectionString = string.Empty;
            if (!string.IsNullOrWhiteSpace(selectedDatabaseProvider))
            {
                switch (selectedDatabaseProvider.ToLowerInvariant())
                {
                    case "postgres":
                    case "postgresql":
                        provider = SupportedDatabaseProviders.Postgres;
                        connectionString = globalSettings.PostgreSql.ConnectionString;
                        break;
                    case "mysql":
                    case "mariadb":
                        provider = SupportedDatabaseProviders.MySql;
                        connectionString = globalSettings.MySql.ConnectionString;
                        break;
                    default:
                        break;
                }
            }

            var useEf = (provider != SupportedDatabaseProviders.SqlServer);

            if (useEf)
            {
                services.AddEFRepositories(globalSettings.SelfHosted, connectionString, provider);
            }
            else
            {
                services.AddDapperRepositories(globalSettings.SelfHosted);
            }

            if (globalSettings.SelfHosted)
            {
                services.AddSingleton<IInstallationDeviceRepository, NoopRepos.InstallationDeviceRepository>();
                services.AddSingleton<IMetaDataRepository, NoopRepos.MetaDataRepository>();
            }
            else
            {
                services.AddSingleton<IEventRepository, TableStorageRepos.EventRepository>();
                services.AddSingleton<IInstallationDeviceRepository, TableStorageRepos.InstallationDeviceRepository>();
                services.AddSingleton<IMetaDataRepository, TableStorageRepos.MetaDataRepository>();
            }
        }

        public static void AddBaseServices(this IServiceCollection services)
        {
            services.AddScoped<ICipherService, CipherService>();
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IOrganizationService, OrganizationService>();
            services.AddScoped<IOrganizationSponsorshipService, OrganizationSponsorshipService>();
            services.AddScoped<ICollectionService, CollectionService>();
            services.AddScoped<IGroupService, GroupService>();
            services.AddScoped<IPolicyService, PolicyService>();
            services.AddScoped<IEventService, EventService>();
            services.AddScoped<IEmergencyAccessService, EmergencyAccessService>();
            services.AddSingleton<IDeviceService, DeviceService>();
            services.AddSingleton<IAppleIapService, AppleIapService>();
            services.AddScoped<ISsoConfigService, SsoConfigService>();
            services.AddScoped<ISendService, SendService>();
        }

        public static void AddTokenizers(this IServiceCollection services)
        {
            services.AddSingleton<IDataProtectorTokenFactory<EmergencyAccessInviteTokenable>>(serviceProvider =>
                new DataProtectorTokenFactory<EmergencyAccessInviteTokenable>(
                    EmergencyAccessInviteTokenable.ClearTextPrefix,
                    EmergencyAccessInviteTokenable.DataProtectorPurpose,
                    serviceProvider.GetDataProtectionProvider())
            );
            services.AddSingleton<IDataProtectorTokenFactory<HCaptchaTokenable>>(serviceProvider =>
                new DataProtectorTokenFactory<HCaptchaTokenable>(
                    HCaptchaTokenable.ClearTextPrefix,
                    HCaptchaTokenable.DataProtectorPurpose,
                    serviceProvider.GetDataProtectionProvider())
            );
        }

        public static void AddDefaultServices(this IServiceCollection services, GlobalSettings globalSettings)
        {
            // Required for UserService
            services.AddWebAuthn(globalSettings);

            services.AddSingleton<IStripeAdapter, StripeAdapter>();
            services.AddSingleton<Braintree.IBraintreeGateway>((serviceProvider) =>
            {
                return new Braintree.BraintreeGateway
                {
                    Environment = globalSettings.Braintree.Production ?
                        Braintree.Environment.PRODUCTION : Braintree.Environment.SANDBOX,
                    MerchantId = globalSettings.Braintree.MerchantId,
                    PublicKey = globalSettings.Braintree.PublicKey,
                    PrivateKey = globalSettings.Braintree.PrivateKey
                };
            });
            services.AddSingleton<IPaymentService, StripePaymentService>();
            services.AddSingleton<IMailService, HandlebarsMailService>();
            services.AddSingleton<ILicensingService, LicensingService>();
            services.AddTokenizers();

            if (CoreHelpers.SettingHasValue(globalSettings.ServiceBus.ConnectionString) &&
                CoreHelpers.SettingHasValue(globalSettings.ServiceBus.ApplicationCacheTopicName))
            {
                services.AddSingleton<IApplicationCacheService, InMemoryServiceBusApplicationCacheService>();
            }
            else
            {
                services.AddSingleton<IApplicationCacheService, InMemoryApplicationCacheService>();
            }

            var awsConfigured = CoreHelpers.SettingHasValue(globalSettings.Amazon?.AccessKeySecret);
            if (awsConfigured && CoreHelpers.SettingHasValue(globalSettings.Mail?.SendGridApiKey))
            {
                services.AddSingleton<IMailDeliveryService, MultiServiceMailDeliveryService>();
            }
            else if (awsConfigured)
            {
                services.AddSingleton<IMailDeliveryService, AmazonSesMailDeliveryService>();
            }
            else if (CoreHelpers.SettingHasValue(globalSettings.Mail?.Smtp?.Host))
            {
                services.AddSingleton<IMailDeliveryService, MailKitSmtpMailDeliveryService>();
            }
            else
            {
                services.AddSingleton<IMailDeliveryService, NoopMailDeliveryService>();
            }

            services.AddSingleton<IPushNotificationService, MultiServicePushNotificationService>();
            if (globalSettings.SelfHosted &&
                CoreHelpers.SettingHasValue(globalSettings.PushRelayBaseUri) &&
                globalSettings.Installation?.Id != null &&
                CoreHelpers.SettingHasValue(globalSettings.Installation?.Key))
            {
                services.AddSingleton<IPushRegistrationService, RelayPushRegistrationService>();
            }
            else if (!globalSettings.SelfHosted)
            {
                services.AddSingleton<IPushRegistrationService, NotificationHubPushRegistrationService>();
            }
            else
            {
                services.AddSingleton<IPushRegistrationService, NoopPushRegistrationService>();
            }

            if (!globalSettings.SelfHosted && CoreHelpers.SettingHasValue(globalSettings.Storage?.ConnectionString))
            {
                services.AddSingleton<IBlockIpService, AzureQueueBlockIpService>();
            }
            else if (!globalSettings.SelfHosted && CoreHelpers.SettingHasValue(globalSettings.Amazon?.AccessKeySecret))
            {
                services.AddSingleton<IBlockIpService, AmazonSqsBlockIpService>();
            }
            else
            {
                services.AddSingleton<IBlockIpService, NoopBlockIpService>();
            }

            if (!globalSettings.SelfHosted && CoreHelpers.SettingHasValue(globalSettings.Mail.ConnectionString))
            {
                services.AddSingleton<IMailEnqueuingService, AzureQueueMailService>();
            }
            else
            {
                services.AddSingleton<IMailEnqueuingService, BlockingMailEnqueuingService>();
            }

            if (!globalSettings.SelfHosted && CoreHelpers.SettingHasValue(globalSettings.Events.ConnectionString))
            {
                services.AddSingleton<IEventWriteService, AzureQueueEventWriteService>();
            }
            else if (globalSettings.SelfHosted)
            {
                services.AddSingleton<IEventWriteService, RepositoryEventWriteService>();
            }
            else
            {
                services.AddSingleton<IEventWriteService, NoopEventWriteService>();
            }

            if (CoreHelpers.SettingHasValue(globalSettings.Attachment.ConnectionString))
            {
                services.AddSingleton<IAttachmentStorageService, AzureAttachmentStorageService>();
            }
            else if (CoreHelpers.SettingHasValue(globalSettings.Attachment.BaseDirectory))
            {
                services.AddSingleton<IAttachmentStorageService, LocalAttachmentStorageService>();
            }
            else
            {
                services.AddSingleton<IAttachmentStorageService, NoopAttachmentStorageService>();
            }

            if (CoreHelpers.SettingHasValue(globalSettings.Send.ConnectionString))
            {
                services.AddSingleton<ISendFileStorageService, AzureSendFileStorageService>();
            }
            else if (CoreHelpers.SettingHasValue(globalSettings.Send.BaseDirectory))
            {
                services.AddSingleton<ISendFileStorageService, LocalSendStorageService>();
            }
            else
            {
                services.AddSingleton<ISendFileStorageService, NoopSendFileStorageService>();
            }

            if (globalSettings.SelfHosted)
            {
                services.AddSingleton<IReferenceEventService, NoopReferenceEventService>();
            }
            else
            {
                services.AddSingleton<IReferenceEventService, AzureQueueReferenceEventService>();
            }

            if (CoreHelpers.SettingHasValue(globalSettings.Captcha?.HCaptchaSecretKey) &&
                CoreHelpers.SettingHasValue(globalSettings.Captcha?.HCaptchaSiteKey))
            {
                services.AddSingleton<ICaptchaValidationService, HCaptchaValidationService>();
            }
            else
            {
                services.AddSingleton<ICaptchaValidationService, NoopCaptchaValidationService>();
            }
        }

        public static void AddOosServices(this IServiceCollection services)
        {
            services.AddScoped<IProviderService, NoopProviderService>();
        }

        public static void AddNoopServices(this IServiceCollection services)
        {
            services.AddSingleton<IMailService, NoopMailService>();
            services.AddSingleton<IMailDeliveryService, NoopMailDeliveryService>();
            services.AddSingleton<IPushNotificationService, NoopPushNotificationService>();
            services.AddSingleton<IBlockIpService, NoopBlockIpService>();
            services.AddSingleton<IPushRegistrationService, NoopPushRegistrationService>();
            services.AddSingleton<IAttachmentStorageService, NoopAttachmentStorageService>();
            services.AddSingleton<ILicensingService, NoopLicensingService>();
            services.AddSingleton<IEventWriteService, NoopEventWriteService>();
        }

        public static IdentityBuilder AddCustomIdentityServices(
            this IServiceCollection services, GlobalSettings globalSettings)
        {
            services.AddSingleton<IOrganizationDuoWebTokenProvider, OrganizationDuoWebTokenProvider>();
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = 100000);
            services.Configure<TwoFactorRememberTokenProviderOptions>(options =>
            {
                options.TokenLifespan = TimeSpan.FromDays(30);
            });

            var identityBuilder = services.AddIdentityWithoutCookieAuth<User, Role>(options =>
            {
                options.User = new UserOptions
                {
                    RequireUniqueEmail = true,
                    AllowedUserNameCharacters = null // all
                };
                options.Password = new PasswordOptions
                {
                    RequireDigit = false,
                    RequireLowercase = false,
                    RequiredLength = 8,
                    RequireNonAlphanumeric = false,
                    RequireUppercase = false
                };
                options.ClaimsIdentity = new ClaimsIdentityOptions
                {
                    SecurityStampClaimType = "sstamp",
                    UserNameClaimType = JwtClaimTypes.Email,
                    UserIdClaimType = JwtClaimTypes.Subject
                };
                options.Tokens.ChangeEmailTokenProvider = TokenOptions.DefaultEmailProvider;
            });

            identityBuilder
                .AddUserStore<UserStore>()
                .AddRoleStore<RoleStore>()
                .AddTokenProvider<DataProtectorTokenProvider<User>>(TokenOptions.DefaultProvider)
                .AddTokenProvider<AuthenticatorTokenProvider>(
                    CoreHelpers.CustomProviderName(TwoFactorProviderType.Authenticator))
                .AddTokenProvider<EmailTokenProvider>(
                    CoreHelpers.CustomProviderName(TwoFactorProviderType.Email))
                .AddTokenProvider<YubicoOtpTokenProvider>(
                    CoreHelpers.CustomProviderName(TwoFactorProviderType.YubiKey))
                .AddTokenProvider<DuoWebTokenProvider>(
                    CoreHelpers.CustomProviderName(TwoFactorProviderType.Duo))
                .AddTokenProvider<TwoFactorRememberTokenProvider>(
                    CoreHelpers.CustomProviderName(TwoFactorProviderType.Remember))
                .AddTokenProvider<EmailTokenProvider<User>>(TokenOptions.DefaultEmailProvider)
                .AddTokenProvider<WebAuthnTokenProvider>(
                    CoreHelpers.CustomProviderName(TwoFactorProviderType.WebAuthn));

            return identityBuilder;
        }

        public static Tuple<IdentityBuilder, IdentityBuilder> AddPasswordlessIdentityServices<TUserStore>(
            this IServiceCollection services, GlobalSettings globalSettings) where TUserStore : class
        {
            services.TryAddTransient<ILookupNormalizer, LowerInvariantLookupNormalizer>();
            services.Configure<DataProtectionTokenProviderOptions>(options =>
            {
                options.TokenLifespan = TimeSpan.FromMinutes(15);
            });

            var passwordlessIdentityBuilder = services.AddIdentity<IdentityUser, Role>()
                .AddUserStore<TUserStore>()
                .AddRoleStore<RoleStore>()
                .AddDefaultTokenProviders();

            var regularIdentityBuilder = services.AddIdentityCore<User>()
                .AddUserStore<UserStore>();

            services.TryAddScoped<PasswordlessSignInManager<IdentityUser>, PasswordlessSignInManager<IdentityUser>>();

            services.ConfigureApplicationCookie(options =>
            {
                options.LoginPath = "/login";
                options.LogoutPath = "/";
                options.AccessDeniedPath = "/login?accessDenied=true";
                options.Cookie.Name = $"Bitwarden_{globalSettings.ProjectName}";
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(2);
                options.ReturnUrlParameter = "returnUrl";
                options.SlidingExpiration = true;
            });

            return new Tuple<IdentityBuilder, IdentityBuilder>(passwordlessIdentityBuilder, regularIdentityBuilder);
        }

        public static void AddIdentityAuthenticationServices(
            this IServiceCollection services, GlobalSettings globalSettings, IWebHostEnvironment environment,
            Action<AuthorizationOptions> addAuthorization)
        {
            services
                .AddAuthentication(IdentityServerAuthenticationDefaults.AuthenticationScheme)
                .AddIdentityServerAuthentication(options =>
                {
                    options.Authority = globalSettings.BaseServiceUri.InternalIdentity;
                    options.RequireHttpsMetadata = !environment.IsDevelopment() &&
                        globalSettings.BaseServiceUri.InternalIdentity.StartsWith("https");
                    options.TokenRetriever = TokenRetrieval.FromAuthorizationHeaderOrQueryString();
                    options.NameClaimType = ClaimTypes.Email;
                    options.SupportedTokens = SupportedTokens.Jwt;
                });

            if (addAuthorization != null)
            {
                services.AddAuthorization(config =>
                {
                    addAuthorization.Invoke(config);
                });
            }

            if (environment.IsDevelopment())
            {
                Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
            }
        }

        public static void AddCustomDataProtectionServices(
            this IServiceCollection services, IWebHostEnvironment env, GlobalSettings globalSettings)
        {
            var builder = services.AddDataProtection(options => options.ApplicationDiscriminator = "Bitwarden");
            if (env.IsDevelopment())
            {
                return;
            }

            if (globalSettings.SelfHosted && CoreHelpers.SettingHasValue(globalSettings.DataProtection.Directory))
            {
                builder.PersistKeysToFileSystem(new DirectoryInfo(globalSettings.DataProtection.Directory));
            }

            if (!globalSettings.SelfHosted && CoreHelpers.SettingHasValue(globalSettings.Storage?.ConnectionString))
            {
                X509Certificate2 dataProtectionCert = null;
                if (CoreHelpers.SettingHasValue(globalSettings.DataProtection.CertificateThumbprint))
                {
                    dataProtectionCert = CoreHelpers.GetCertificate(
                        globalSettings.DataProtection.CertificateThumbprint);
                }
                else if (CoreHelpers.SettingHasValue(globalSettings.DataProtection.CertificatePassword))
                {
                    dataProtectionCert = CoreHelpers.GetBlobCertificateAsync(globalSettings.Storage.ConnectionString, "certificates",
                        "dataprotection.pfx", globalSettings.DataProtection.CertificatePassword)
                        .GetAwaiter().GetResult();
                }
                //TODO djsmith85 Check if this is the correct container name
                builder
                    .PersistKeysToAzureBlobStorage(globalSettings.Storage.ConnectionString, "aspnet-dataprotection", "keys.xml")
                    .ProtectKeysWithCertificate(dataProtectionCert);
            }
        }

        public static IIdentityServerBuilder AddIdentityServerCertificate(
            this IIdentityServerBuilder identityServerBuilder, IWebHostEnvironment env, GlobalSettings globalSettings)
        {
            var certificate = CoreHelpers.GetIdentityServerCertificate(globalSettings);
            if (certificate != null)
            {
                identityServerBuilder.AddSigningCredential(certificate);
            }
            else if (env.IsDevelopment())
            {
                identityServerBuilder.AddDeveloperSigningCredential(false);
            }
            else
            {
                throw new Exception("No identity certificate to use.");
            }
            return identityServerBuilder;
        }

        public static GlobalSettings AddGlobalSettingsServices(this IServiceCollection services,
            IConfiguration configuration, IWebHostEnvironment environment)
        {
            var globalSettings = new GlobalSettings();
            ConfigurationBinder.Bind(configuration.GetSection("GlobalSettings"), globalSettings);

            if (environment.IsDevelopment() && configuration.GetValue<bool>("developSelfHosted"))
            {
                // Override settings with selfHostedOverride settings
                ConfigurationBinder.Bind(configuration.GetSection("Dev:SelfHostOverride:GlobalSettings"), globalSettings);
            }

            services.AddSingleton(s => globalSettings);
            services.AddSingleton<IGlobalSettings, GlobalSettings>(s => globalSettings);
            return globalSettings;
        }

        public static void UseDefaultMiddleware(this IApplicationBuilder app,
            IWebHostEnvironment env, GlobalSettings globalSettings)
        {
            string GetHeaderValue(HttpContext httpContext, string header)
            {
                if (httpContext.Request.Headers.ContainsKey(header))
                {
                    return httpContext.Request.Headers[header];
                }
                return null;
            }

            // Add version information to response headers
            app.Use(async (httpContext, next) =>
            {
                using (LogContext.PushProperty("IPAddress", httpContext.GetIpAddress(globalSettings)))
                using (LogContext.PushProperty("UserAgent", GetHeaderValue(httpContext, "user-agent")))
                using (LogContext.PushProperty("DeviceType", GetHeaderValue(httpContext, "device-type")))
                using (LogContext.PushProperty("Origin", GetHeaderValue(httpContext, "origin")))
                {
                    httpContext.Response.OnStarting((state) =>
                    {
                        httpContext.Response.Headers.Append("Server-Version", CoreHelpers.GetVersion());
                        return Task.FromResult(0);
                    }, null);
                    await next.Invoke();
                }
            });
        }

        public static void UseForwardedHeaders(this IApplicationBuilder app, GlobalSettings globalSettings)
        {
            var options = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            if (!string.IsNullOrWhiteSpace(globalSettings.KnownProxies))
            {
                var proxies = globalSettings.KnownProxies.Split(',');
                foreach (var proxy in proxies)
                {
                    if (System.Net.IPAddress.TryParse(proxy.Trim(), out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }
            }
            if (options.KnownProxies.Count > 1)
            {
                options.ForwardLimit = null;
            }
            app.UseForwardedHeaders(options);
        }

        public static void AddCoreLocalizationServices(this IServiceCollection services)
        {
            services.AddTransient<II18nService, I18nService>();
            services.AddLocalization(options => options.ResourcesPath = "Resources");
        }

        public static IApplicationBuilder UseCoreLocalization(this IApplicationBuilder app)
        {
            var supportedCultures = new[] { "en" };
            return app.UseRequestLocalization(options => options
                .SetDefaultCulture(supportedCultures[0])
                .AddSupportedCultures(supportedCultures)
                .AddSupportedUICultures(supportedCultures));
        }

        public static IMvcBuilder AddViewAndDataAnnotationLocalization(this IMvcBuilder mvc)
        {
            mvc.Services.AddTransient<IViewLocalizer, I18nViewLocalizer>();
            return mvc.AddViewLocalization(options => options.ResourcesPath = "Resources")
                .AddDataAnnotationsLocalization(options =>
                    options.DataAnnotationLocalizerProvider = (type, factory) =>
                    {
                        var assemblyName = new AssemblyName(typeof(SharedResources).GetTypeInfo().Assembly.FullName);
                        return factory.Create("SharedResources", assemblyName.Name);
                    });
        }

        public static IServiceCollection AddDistributedIdentityServices(this IServiceCollection services, GlobalSettings globalSettings)
        {
            if (string.IsNullOrWhiteSpace(globalSettings.IdentityServer?.RedisConnectionString))
            {
                services.AddDistributedMemoryCache();
            }
            else
            {
                services.AddDistributedRedisCache(options =>
                    options.Configuration = globalSettings.IdentityServer.RedisConnectionString);
            }

            services.AddOidcStateDataFormatterCache();
            services.AddSession();
            services.ConfigureApplicationCookie(configure => configure.CookieManager = new DistributedCacheCookieManager());
            services.ConfigureExternalCookie(configure => configure.CookieManager = new DistributedCacheCookieManager());
            services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>>(
                svcs => new ConfigureOpenIdConnectDistributedOptions(
                    svcs.GetRequiredService<IHttpContextAccessor>(),
                    globalSettings,
                    svcs.GetRequiredService<IdentityServerOptions>())
            );

            return services;
        }

        public static void AddWebAuthn(this IServiceCollection services, GlobalSettings globalSettings)
        {
            services.AddFido2(options =>
            {
                options.ServerDomain = new Uri(globalSettings.BaseServiceUri.Vault).Host;
                options.ServerName = "Bitwarden";
                options.Origin = globalSettings.BaseServiceUri.Vault;
                options.TimestampDriftTolerance = 300000;
            });
        }
    }
}
