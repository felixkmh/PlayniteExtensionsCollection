﻿using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PluginsCommon;
using PluginsCommon.Web;
using SteamWishlistDiscountNotifier.Enums;
using SteamWishlistDiscountNotifier.Models;
using SteamWishlistDiscountNotifier.ViewModels;
using SteamWishlistDiscountNotifier.Views;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SteamWishlistDiscountNotifier
{
    public class SteamWishlistDiscountNotifier : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly int currentDatabaseVersion = 1;
        private static readonly Regex discBlockParseRegex = new Regex(@"discount_original_price"">([^<]+).+(?=discount_final_price)discount_final_price"">([^<]+)", RegexOptions.Compiled);
        private static readonly Regex discBlockNoDiscountParseRegex = new Regex(@"discount_final_price"">([^<]+)", RegexOptions.Compiled);
        private static char[] numberChars = {'0','1', '2', '3', '4', '5', '6', '7', '8', '9'};
        private const string steamStoreUrlMask = @"https://store.steampowered.com/app/{0}/";
        private const string steamUriOpenUrlMask = @"steam://openurl/{0}";
        private const string steamWishlistUrlMask = @"https://store.steampowered.com/wishlist/profiles/{0}/wishlistdata/?p={1}";
        private const string notLoggedInNotifId = @"Steam_Wishlist_Notif_AuthRequired";
        private const string webViewUserAgent = @"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36 Vivaldi/4.3";
        private readonly string pluginInstallPath;
        private readonly string wishlistCachePath;
        private readonly string bannerImagesCachePath;
        public readonly DispatcherTimer wishlistCheckTimer;
        private int authRequiredTicks = 0;

        private SteamWishlistDiscountNotifierSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("d5825a82-42bf-426b-ac47-5bea5df7aede");

        public SteamWishlistDiscountNotifier(IPlayniteAPI api) : base(api)
        {
            settings = new SteamWishlistDiscountNotifierSettingsViewModel(this);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            pluginInstallPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            wishlistCachePath = Path.Combine(GetPluginUserDataPath(), "WishlistCache.json");
            bannerImagesCachePath = Path.Combine(GetPluginUserDataPath(), "BannerImages");
            wishlistCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1),
            };
            wishlistCheckTimer.Tick += new EventHandler(WishlistCheckTimer_Tick);
        }

        private void WishlistCheckTimer_Tick(object sender, EventArgs e)
        {
            // Done to prevent constantly checking for the wishlist when
            // the user has been detected to not have been authenticated
            // Since cookies are shared between extensions, it's possible that
            // the user authenticates via other plugin, which would cause this
            // plugin to not know the new status. For this reason, we only stop
            // checking for maximum 10 minutes before checking again
            if (settings.CheckedStatus == AuthStatus.AuthRequired &&
                authRequiredTicks < 10)
            {
                authRequiredTicks++;
                return;
            }

            authRequiredTicks = 0;
            if (!settings.Settings.EnableWishlistNotifications)
            {
                return;
            }

            if (DateTime.Now >
                 settings.Settings.LastWishlistUpdate.AddMinutes(settings.Settings.WishlistAutoCheckIntervalMins))
            {
                StartWishlistCheckTask();
            }
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = ResourceProvider.GetString("LOCSteam_Wishlist_Notif_SidebarTitle"),
                Type = SiderbarItemType.View,
                Icon = new TextBlock
                {
                    Text = char.ConvertFromUtf32(0xed71),
                    FontSize = 20,
                    FontFamily = ResourceProvider.GetResource("FontIcoFont") as FontFamily
                },
                Opened = () => {
                    return GetSteamWishlistViewerSidebarView();
                }
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCSteam_Wishlist_Notif_MenuItemStartWishlistDiscountCheckDescription"),
                    MenuSection = "@Steam Wishlist Discount Notifier",
                    Action = aca => {
                        PlayniteApi.Dialogs.ActivateGlobalProgress((a) =>
                        {
                            StartWishlistCheck();
                        }, new GlobalProgressOptions(ResourceProvider.GetString("LOCSteam_Wishlist_Notif_ObtainingWishlistMessage"), true));
                    }
                }
            };
        }

        private SteamWishlistViewerView GetSteamWishlistViewerSidebarView()
        {
            var wishlistItems = new List<WishlistItemCache>();
            var tokenWasCancelled = false;
            PlayniteApi.Dialogs.ActivateGlobalProgress((a) =>
            {
                wishlistItems = GetSteamCompleteWishlist(a);
                SetWishlistItemsBannerPaths(wishlistItems, a);
                tokenWasCancelled = a.CancelToken.IsCancellationRequested;
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOCSteam_Wishlist_Notif_ObtainingWishlistMessage"), true));

            if (wishlistItems == null || tokenWasCancelled)
            {
                return null;
            }
            else
            {
                return new SteamWishlistViewerView { DataContext = new SteamWishlistViewerViewModel(PlayniteApi, wishlistItems, pluginInstallPath) };
            }
        }

        private void SetWishlistItemsBannerPaths(List<WishlistItemCache> wishlistItems, GlobalProgressActionArgs a)
        {
            if (wishlistItems == null)
            {
                return;
            }
            
            foreach (var wishlistItem in wishlistItems)
            {
                if (a.CancelToken.IsCancellationRequested)
                {
                    return;
                }
                
                var bannerPath = Path.Combine(bannerImagesCachePath, wishlistItem.StoreId + ".jpg");
                if (File.Exists(bannerPath))
                {
                    wishlistItem.BannerImagePath = bannerPath;
                    continue;
                }

                try
                {
                    HttpDownloader.DownloadFile(wishlistItem.WishlistItem.Capsule.ToString(), bannerPath);
                    wishlistItem.BannerImagePath = bannerPath;
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Error downloading banner image {wishlistItem.WishlistItem.Capsule.ToString()}");
                }
            }
        }

        private List<WishlistItemCache> GetSteamCompleteWishlist(GlobalProgressActionArgs a)
        {
            using (var webView = PlayniteApi.WebViews.CreateOffscreenView(new WebViewSettings{UserAgent = webViewUserAgent}))
            {
                SteamLogin.GetLoggedInSteamId64(webView, out var status, out var steamId);
                settings.CheckedStatus = status;
                logger.Debug($"Started checking for wishlist. Status: {status}, steamId: {steamId}");
                if (status == AuthStatus.NoConnection)
                {
                    return null;
                }
                else if (status == AuthStatus.Ok)
                {
                    PlayniteApi.Notifications.Remove(notLoggedInNotifId);
                    var wishlistItems = GetWishlistDiscounts(steamId, webView, true, a);
                    if (wishlistItems == null)
                    {
                        return null;
                    }
                    else
                    {
                        return wishlistItems;
                    }
                }
                else if (status == AuthStatus.AuthRequired)
                {
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        notLoggedInNotifId,
                        ResourceProvider.GetString("LOCSteam_Wishlist_Notif_WishlistCheckNotLoggedIn"),
                        NotificationType.Info,
                        () => OpenSettingsView()
                    ));
                }
            }

            return null;
        }

        private void StartWishlistCheckTask()
        {
            Task.Run(() =>
            {
                StartWishlistCheck();
            });
        }

        private void StartWishlistCheck()
        {
            wishlistCheckTimer.Stop();
            using (var webView = PlayniteApi.WebViews.CreateOffscreenView(new WebViewSettings { UserAgent = webViewUserAgent }))
            {
                SteamLogin.GetLoggedInSteamId64(webView, out var status, out var steamId);
                settings.CheckedStatus = status;
                logger.Debug($"Started checking for wishlist. Status: {status}, steamId: {steamId}");
                if (status == AuthStatus.Ok)
                {
                    PlayniteApi.Notifications.Remove(notLoggedInNotifId);
                    UpdateAndNotifyWishlistDiscounts(steamId, webView);
                }
                else if (status == AuthStatus.AuthRequired)
                {
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        notLoggedInNotifId,
                        ResourceProvider.GetString("LOCSteam_Wishlist_Notif_WishlistCheckNotLoggedIn"),
                        NotificationType.Info,
                        () => OpenSettingsView()
                    ));
                }
            }

            logger.Debug($"Finished checking for wishlist");
            wishlistCheckTimer.Start();
        }

        private int? UpdateAndNotifyWishlistDiscounts(string steamId, IWebView webView)
        {
            var wishlistItems = GetWishlistDiscounts(steamId, webView, false);
            if (wishlistItems == null)
            {
                return null;
            }

            var wishlistCache = GetWishlistCache();
            var cacheRemovals = 0;
            var cacheAdditions = 0;

            // Check for changes in existing cache
            var wishlistDiscounts = new Dictionary<double, WishlistItemCache>();
            foreach (var wishlistItem in wishlistItems)
            {
                wishlistDiscounts[(double)wishlistItem.SubId] = wishlistItem;
            }

            foreach (var cachedDiscount in wishlistCache.ToList())
            {
                if (wishlistDiscounts.TryGetValue((double)cachedDiscount.SubId, out var newDiscount))
                {
                    if (HasDiscountDataChanged(cachedDiscount, newDiscount))
                    {
                        wishlistCache.Remove(cachedDiscount);
                        cacheRemovals++;
                        wishlistCache.Add(newDiscount);
                        cacheAdditions++;

                        AddNotifyDiscount(newDiscount);
                    }
                }
                else
                {
                    cacheRemovals++;
                    wishlistCache.Remove(cachedDiscount);
                }
            }

            // Check new items in discount
            foreach (var wishlistDiscount in wishlistDiscounts)
            {
                if (!wishlistCache.Any(x => x.SubId == wishlistDiscount.Key))
                {
                    wishlistCache.Add(wishlistDiscount.Value);
                    cacheAdditions++;
                    AddNotifyDiscount(wishlistDiscount.Value);
                }
            }

            if (cacheRemovals > 0 || cacheAdditions > 0)
            {
                FileSystem.WriteStringToFile(wishlistCachePath, Serialization.ToJson(wishlistCache));
            }

            settings.Settings.LastWishlistUpdate = DateTime.Now;
            SavePluginSettings(settings.Settings);

            return cacheRemovals + cacheAdditions;
        }

        private void AddNotifyDiscount(WishlistItemCache newDiscount)
        {
            if (!GetShouldDisplayNotification(newDiscount))
            {
                return;
            }
            
            PlayniteApi.Notifications.Add(new NotificationMessage(
                Guid.NewGuid().ToString(),
                GetDiscountNotificationMessage(newDiscount),
                NotificationType.Info,
                () => OpenDiscountedItemUrl(newDiscount.StoreId)
            ));
        }

        private bool GetShouldDisplayNotification(WishlistItemCache newDiscount)
        {
            if (settings.Settings.NotificationMinDiscount > newDiscount.DiscountPercent)
            {
                return false;
            }

            switch (newDiscount.WishlistItem.Type)
            {
                case StoreItemType.Game:
                    return settings.Settings.NotifyDiscountsTypeGame;
                case StoreItemType.Dlc:
                    return settings.Settings.NotifyDiscountsTypeDlc;
                case StoreItemType.Music:
                    return settings.Settings.NotifyDiscountsTypeMusic;
                case StoreItemType.Application:
                    return settings.Settings.NotifyDiscountsTypeApplication;
                case StoreItemType.Video:
                    return settings.Settings.NotifyDiscountsTypeVideo;
                case StoreItemType.Series:
                    return settings.Settings.NotifyDiscountsTypeVideo;
                case StoreItemType.Hardware:
                    return settings.Settings.NotifyDiscountsTypeHardware;
                case StoreItemType.Mod:
                    return settings.Settings.NotifyDiscountsTypeMod;
                default:
                    return true;
            }
        }

        private void OpenDiscountedItemUrl(string steamStoreId)
        {
            var subIdSteamUrl = string.Format(steamStoreUrlMask, steamStoreId);
            if (settings.Settings.OpenUrlsInBrowser)
            {
                ProcessStarter.StartUrl(subIdSteamUrl);
            }
            else
            {
                ProcessStarter.StartUrl(string.Format(steamUriOpenUrlMask, subIdSteamUrl));
            }
        }

        private string GetDiscountNotificationMessage(WishlistItemCache newDiscount)
        {
            return string.Join("\n", new string[3]
            {
                string.Format(ResourceProvider.GetString("LOCSteam_Wishlist_Notif_GameOnSaleLabel"), newDiscount.Name) + "\n",
                string.Format(ResourceProvider.GetString("LOCSteam_Wishlist_Notif_DiscountPercent"), newDiscount.DiscountPercent),
                string.Format("{0} {1} -> {0} {2}", newDiscount.Currency, ((double)newDiscount.PriceOriginal).ToString("0.00"), ((double)newDiscount.PriceFinal).ToString("0.00"))
            });
        }

        private bool HasDiscountDataChanged(WishlistItemCache cachedDiscount, WishlistItemCache newDiscount)
        {
            if (cachedDiscount.PriceFinal != newDiscount.PriceFinal ||
                cachedDiscount.Currency != newDiscount.Currency ||
                cachedDiscount.DiscountPercent != newDiscount.DiscountPercent)
            {
                return true;
            }

            return false;
        }

        private List<WishlistItemCache> GetWishlistCache()
        {
            if (FileSystem.FileExists(wishlistCachePath))
            {
                return Serialization.FromJsonFile<List<WishlistItemCache>>(wishlistCachePath);
            }

            return new List<WishlistItemCache>();
        }

        private List<WishlistItemCache> GetWishlistDiscounts(string steamId, IWebView webView, bool getNonDiscountedItems, GlobalProgressActionArgs a = null)
        {
            var currentPage = 0;
            var wishlistItems = new List<WishlistItemCache>();
            while (true)
            {
                if (a?.CancelToken.IsCancellationRequested == true)
                {
                    return null;
                }
                
                var pageSource = GetWishlistPageSource(webView, steamId, currentPage);
                if (pageSource == null)
                {
                    return null;
                }
                else if (pageSource == string.Empty)
                {
                    break;
                }

                var response = Serialization.FromJson<Dictionary<string, SteamWishlistItem>>(pageSource);
                foreach (var wishlistItem in response.Values)
                {
                    wishlistItem.ReleaseString = HttpUtility.HtmlDecode(wishlistItem.ReleaseString);
                    if (wishlistItem.Subs.HasItems())
                    {
                        foreach (var sub in wishlistItem.Subs)
                        {
                            AddWishlistItemToList(wishlistItems, wishlistItem, sub, getNonDiscountedItems);
                        }
                    }
                    else if (getNonDiscountedItems)
                    {
                        AddWishlistItemNoSubsToList(wishlistItems, wishlistItem);
                    }
                }

                currentPage++;
                Thread.Sleep(1000);
            }

            logger.Debug($"Wishlist check obtained {wishlistItems.Count} items, {getNonDiscountedItems}");
            return wishlistItems;
        }

        private void AddWishlistItemNoSubsToList(List<WishlistItemCache> wishlistItems, SteamWishlistItem wishlistItem)
        {
            wishlistItems.Add(new WishlistItemCache
            {
                Name = HttpUtility.HtmlDecode(wishlistItem.Name),
                StoreId = Regex.Match(wishlistItem.Capsule.ToString(), @"apps\/(\d+)\/header").Groups[1].Value,
                SubId = null,
                PriceOriginal = null,
                PriceFinal = null,
                Currency = null,
                DiscountPercent = 0,
                WishlistItem = wishlistItem,
                IsDiscounted = false
            });
        }

        private static string GetWishlistPageSource(IWebView webView, string steamId, int currentPage)
        {
            var url = string.Format(steamWishlistUrlMask, steamId, currentPage);
            webView.NavigateAndWait(url);
            var pageSource = webView.GetPageSource();
            pageSource = HttpUtility.HtmlDecode(pageSource);
            var startIndex = pageSource.IndexOf('{');
            var endIndex = pageSource.LastIndexOf('}'); ;
            if (startIndex == -1 || endIndex == -1)
            {
                logger.Debug($"Wishlist check finished in {url}");
                return string.Empty;
            }

            pageSource = pageSource.Substring(startIndex, endIndex - startIndex + 1);
            if (pageSource.IsNullOrEmpty() || pageSource == "[]")
            {
                return string.Empty;
            }

            // Success 2 means that the logged account doesn't have permissions to check the
            // wishlist. Check in case logged account has changed in the period between obtaining
            // the steamId and getting the wishlist
            if (pageSource == @"{""success"":2}")
            {
                logger.Warn($"Page {url}, Success 2");
                return null;
            }

            return pageSource;
        }

        private void AddWishlistItemToList(List<WishlistItemCache> wishlistItems, SteamWishlistItem wishlistItem, Sub sub, bool getNonDiscountedItems)
        {
            if (sub.DiscountPct == 0)
            {
                if (!getNonDiscountedItems)
                {
                    return;
                }

                var nonDiscountedItem = GetNonDiscountedItemFromSub(wishlistItem, sub);
                if (nonDiscountedItem == null)
                {
                    return;
                }

                wishlistItems.Add(nonDiscountedItem);
            }
            else
            {
                var discountedItem = GetDiscountedItemFromSub(wishlistItem, sub);
                if (discountedItem == null)
                {
                    return;
                }

                wishlistItems.Add(discountedItem);
            }
        }

        private WishlistItemCache GetNonDiscountedItemFromSub(SteamWishlistItem wishlistItem, Sub sub)
        {
            var regexMatch = discBlockNoDiscountParseRegex.Match(sub.DiscountBlock);
            if (!regexMatch.Success)
            {
                logger.Warn($"GetNonDiscountedItemFromSub Failed to parse sub discount block: {sub.DiscountBlock}");
                return null;
            }

            GetPriceValues(regexMatch.Groups[1].Value, out var currencyCode, out var priceFinal);
            if (currencyCode.IsNullOrEmpty() || priceFinal == null)
            {
                return null;
            }

            return new WishlistItemCache
            {
                Name = HttpUtility.HtmlDecode(wishlistItem.Name),
                StoreId = Regex.Match(wishlistItem.Capsule.ToString(), @"apps\/(\d+)\/header").Groups[1].Value,
                SubId = sub.Id,
                PriceOriginal = priceFinal,
                PriceFinal = priceFinal,
                Currency = currencyCode,
                DiscountPercent = sub.DiscountPct,
                WishlistItem = wishlistItem,
                IsDiscounted = false
            };
        }

        private WishlistItemCache GetDiscountedItemFromSub(SteamWishlistItem wishlistItem, Sub sub)
        {
            var regexMatch = discBlockParseRegex.Match(sub.DiscountBlock);
            if (!regexMatch.Success)
            {
                logger.Warn($"GetDiscountedItemFromSub Failed to parse sub discount block : {sub.DiscountBlock}");
                return null;
            }

            GetPriceValues(regexMatch.Groups[1].Value, out var _, out var priceOriginal);
            GetPriceValues(regexMatch.Groups[2].Value, out var currencyCode, out var priceFinal);
            if (currencyCode.IsNullOrEmpty() || priceOriginal == null || priceFinal == null)
            {
                return null;
            }

            return new WishlistItemCache
            {
                Name = HttpUtility.HtmlDecode(wishlistItem.Name),
                StoreId = Regex.Match(wishlistItem.Capsule.ToString(), @"apps\/(\d+)\/header").Groups[1].Value,
                SubId = sub.Id,
                PriceOriginal = priceOriginal,
                PriceFinal = priceFinal,
                Currency = currencyCode,
                DiscountPercent = sub.DiscountPct,
                WishlistItem = wishlistItem,
                IsDiscounted = true
            };
        }

        private void GetPriceValues(string parsedBlock, out string currencyCode, out double? currencyValue)
        {
            parsedBlock = parsedBlock.Trim();
            var firstNumberIndex = parsedBlock.IndexOfAny(numberChars);
            var lastNumberIndex = parsedBlock.LastIndexOfAny(numberChars);

            if (firstNumberIndex == -1 || lastNumberIndex == -1)
            {
                logger.Error($"Failed to parsed money parsed block \"{parsedBlock}\", firstNumberIndex {firstNumberIndex}, lastNumberIndex {lastNumberIndex}");
                currencyCode = null;
                currencyValue = null;
                return;
            }

            var currencyValueStr = parsedBlock.Substring(firstNumberIndex, lastNumberIndex - firstNumberIndex + 1);
            currencyValue = GetParsedPrice(currencyValueStr);
            currencyCode = parsedBlock.Remove(firstNumberIndex, lastNumberIndex - firstNumberIndex + 1).Trim();
        }

        private double GetParsedPrice(string str)
        {
            var pointIndex = str.LastIndexOf('.');
            var commaIndex = str.LastIndexOf(',');
            
            if (commaIndex < pointIndex)
            {
                // Point is decimal separator
                return double.Parse(str, CultureInfo.InvariantCulture);
            }
            else
            {
                // Comma is decimal separator
                return double.Parse(str, CultureInfo.GetCultureInfo("es-ES"));
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if (settings.Settings.DatabaseVersion < currentDatabaseVersion)
            {
                if (FileSystem.FileExists(wishlistCachePath))
                {
                    FileSystem.DeleteFileSafe(wishlistCachePath);
                }

                settings.Settings.DatabaseVersion = currentDatabaseVersion;
                SavePluginSettings(settings.Settings);
            }

            if (settings.Settings.EnableWishlistNotifications &&
                (!FileSystem.FileExists(wishlistCachePath) ||
                  DateTime.Now > settings.Settings.LastWishlistUpdate.AddMinutes(settings.Settings.WishlistAutoCheckIntervalMins)))
            {
                StartWishlistCheckTask();
            }
            else
            {
                wishlistCheckTimer.Start();
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SteamWishlistDiscountNotifierSettingsView();
        }
    }
}