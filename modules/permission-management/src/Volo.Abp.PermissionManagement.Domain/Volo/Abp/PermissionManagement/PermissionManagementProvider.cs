﻿using System.Threading.Tasks;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;

namespace Volo.Abp.PermissionManagement
{
    public abstract class PermissionManagementProvider : IPermissionManagementProvider
    {
        public abstract string Name { get; }

        protected IPermissionGrantRepository PermissionGrantRepository { get; }

        protected IPermissionStore PermissionStore { get; }

        protected IGuidGenerator GuidGenerator { get; }

        protected ICurrentTenant CurrentTenant { get; }

        protected PermissionManagementProvider(
            IPermissionGrantRepository permissionGrantRepository,
            IPermissionStore permissionStore,
            IGuidGenerator guidGenerator,
            ICurrentTenant currentTenant)
        {
            PermissionGrantRepository = permissionGrantRepository;
            PermissionStore = permissionStore;
            GuidGenerator = guidGenerator;
            CurrentTenant = currentTenant;
        }

        public virtual async Task<PermissionValueProviderGrantInfo> CheckAsync(string name, string providerName, string providerKey)
        {
            if (providerName != Name)
            {
                return PermissionValueProviderGrantInfo.NonGranted;
            }

            return new PermissionValueProviderGrantInfo(
                await PermissionStore.IsGrantedAsync(name, providerName, providerKey),
                providerKey
            );
        }

        public virtual Task SetAsync(string name, string providerKey, bool isGranted)
        {
            return isGranted
                ? GrantAsync(name, providerKey)
                : RevokeAsync(name, providerKey);
        }

        protected virtual async Task GrantAsync(string name, string providerKey)
        {
            var permissionGrant = await PermissionGrantRepository.FindAsync(name, Name, providerKey);
            if (permissionGrant != null)
            {
                return;
            }

            await PermissionGrantRepository.InsertAsync(
                new PermissionGrant(
                    GuidGenerator.Create(),
                    name,
                    Name,
                    providerKey,
                    CurrentTenant.Id
                )
            );
        }

        protected virtual async Task RevokeAsync(string name, string providerKey)
        {
            var permissionGrant = await PermissionGrantRepository.FindAsync(name, Name, providerKey);
            if (permissionGrant == null)
            {
                return;
            }

            await PermissionGrantRepository.DeleteAsync(permissionGrant);
        }
    }
}
