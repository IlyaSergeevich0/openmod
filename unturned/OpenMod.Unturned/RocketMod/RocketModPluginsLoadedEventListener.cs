﻿using System;
using System.Diagnostics.CodeAnalysis;
using OpenMod.API.Eventing;
using OpenMod.Core.Eventing;
using OpenMod.Unturned.RocketMod.Events;
using SDG.Unturned;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autofac;
using HarmonyLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenMod.Core.Helpers;
using OpenMod.Core.Ioc;
using OpenMod.Core.Users;
using OpenMod.Extensions.Economy.Abstractions;
using OpenMod.Unturned.Configuration;
using OpenMod.Unturned.RocketMod.Economy;
using OpenMod.Unturned.RocketMod.Economy.Patches;
using OpenMod.Unturned.RocketMod.Permissions;

#pragma warning disable IDE0079 // Remove unnecessary suppression
namespace OpenMod.Unturned.RocketMod
{
    public class RocketModPluginsLoadedEventListener : IEventListener<RocketModPluginsLoadedEvent>
    {
        private const BindingFlags c_BindingFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;

        private readonly ILoggerFactory m_LoggerFactory;
        private readonly IRocketModComponent m_RocketModComponent;
        private readonly IOpenModUnturnedConfiguration m_UnturnedConfiguration;

        private static bool s_RocketListenersRemoved;

        public RocketModPluginsLoadedEventListener(
            ILoggerFactory loggerFactory,
            IRocketModComponent rocketModComponent,
            IOpenModUnturnedConfiguration unturnedConfiguration)
        {
            m_LoggerFactory = loggerFactory;
            m_RocketModComponent = rocketModComponent;
            m_UnturnedConfiguration = unturnedConfiguration;
        }

        [EventListener]
        public Task HandleEventAsync(object sender, RocketModPluginsLoadedEvent @event)
        {
            var permissionSystem = m_UnturnedConfiguration.Configuration
                .GetSection("rocketmodIntegration:permissionSystem")
                .Get<string>();

            if (permissionSystem.Equals("OpenMod", StringComparison.OrdinalIgnoreCase))
            {
                var scope = m_RocketModComponent.LifetimeScope;
                var permissionProxyProvider = ActivatorUtilitiesEx.CreateInstance<RocketModPermissionProxyProvider>(scope);
                permissionProxyProvider.Install();
            }

            var economySystem = m_UnturnedConfiguration.Configuration
                .GetSection("rocketmodIntegration:economySystem")
                .Get<string>();

            if (economySystem.Equals("OpenMod_Uconomy", StringComparison.OrdinalIgnoreCase))
            {
                var uconomyAssembly = UconomyIntegration.GetUconomyAssembly();
                if (uconomyAssembly != null)
                {
                    var databaseType = uconomyAssembly.GetType("fr34kyn01535.Uconomy.DatabaseManager");
                    var getBalanceMethod = databaseType.GetMethod("GetBalance", c_BindingFlags);
                    var increaseBalanceMethod = databaseType.GetMethod("IncreaseBalance", c_BindingFlags);

                    var getBalancePrefixMethod = typeof(UconomyGetBalancePatch)
                        .GetMethod(nameof(UconomyGetBalancePatch.GetBalancePrefix), c_BindingFlags);

                    var increaseBalancePrefixMethod = typeof(UconomyBalanceIncreasePatch)
                        .GetMethod(nameof(UconomyBalanceIncreasePatch.IncreaseBalancePrefix), c_BindingFlags);

                    var harmonyInstance = new Harmony(UconomyIntegration.HarmonyId);
                    harmonyInstance.Patch(getBalanceMethod, prefix: new HarmonyMethod(getBalancePrefixMethod));
                    harmonyInstance.Patch(increaseBalanceMethod, prefix: new HarmonyMethod(increaseBalancePrefixMethod));

                    UconomyGetBalancePatch.OnPreGetBalance += OnPreGetBalance;
                    UconomyBalanceIncreasePatch.OnPreIncreaseBalance += OnPreIncreaseBalance;

                    m_RocketModComponent.LifetimeScope.Disposer.AddInstanceForDisposal(new DisposeAction(() =>
                    {
                        harmonyInstance.UnpatchAll(UconomyIntegration.HarmonyId);

                        UconomyGetBalancePatch.OnPreGetBalance -= OnPreGetBalance;
                        UconomyBalanceIncreasePatch.OnPreIncreaseBalance -= OnPreIncreaseBalance;
                    }));
                }
                else
                {
                    var logger = m_LoggerFactory.CreateLogger<RocketModIntegration>();
                    logger.LogWarning("Economy system was set to OpenMod_Uconomy but Uconomy is not loaded. Defaulting to Separate.");
                }
            }

            RemoveRocketCommandListeners();
            return Task.CompletedTask;
        }

        private void OnPreIncreaseBalance(string id, decimal increaseby, ref decimal newbalance)
        {
            var economyProvider = m_RocketModComponent.LifetimeScope.Resolve<IEconomyProvider>();
            AsyncHelper.RunSync(() => economyProvider.UpdateBalanceAsync(id, KnownActorTypes.Player, increaseby, null));
        }

        private void OnPreGetBalance(string id, ref decimal balance)
        {
            var economyProvider = m_RocketModComponent.LifetimeScope.Resolve<IEconomyProvider>();
            balance = AsyncHelper.RunSync(() => economyProvider.GetBalanceAsync(id, KnownActorTypes.Player));
        }

        [SuppressMessage("ReSharper", "DelegateSubtraction")]
        private void RemoveRocketCommandListeners()
        {
            if (s_RocketListenersRemoved)
            {
                return;
            }

            var commandWindowInputedInvocationList = CommandWindow.onCommandWindowInputted.GetInvocationList();
            foreach (var @delegate in commandWindowInputedInvocationList
                .Where(IsRocketModDelegate))
            {
                CommandWindow.onCommandWindowInputted -= (CommandWindowInputted)@delegate;
            }

            var checkPermissionsList = ChatManager.onCheckPermissions.GetInvocationList();
            foreach (var @delegate in checkPermissionsList
                .Where(IsRocketModDelegate))
            {
                ChatManager.onCheckPermissions -= (CheckPermissions)@delegate;
            }

            s_RocketListenersRemoved = true;
        }

        private bool IsRocketModDelegate(Delegate @delegate)
        {
            if (@delegate == null)
            {
                return false;
            }

            var methodInfo = @delegate.GetMethodInfo();
            var assembly = methodInfo?.DeclaringType?.Assembly;

            if (assembly == null)
            {
                return false;
            }

            return RocketModIntegration.IsRocketModAssembly(assembly);
        }
    }
}