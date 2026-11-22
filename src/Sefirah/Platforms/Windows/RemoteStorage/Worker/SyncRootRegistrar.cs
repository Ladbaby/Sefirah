using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using Sefirah.Platforms.Windows.Abstractions;
using Sefirah.Platforms.Windows.Interop;
using Sefirah.Platforms.Windows.RemoteStorage.Commands;
using Sefirah.Platforms.Windows.RemoteStorage.Configuration;
using Sefirah.Platforms.Windows.RemoteStorage.Sftp;
using Windows.Security.Cryptography;
using Windows.Storage.Provider;

namespace Sefirah.Platforms.Windows.RemoteStorage.Worker;

public class SyncRootRegistrar(
    IOptions<ProviderOptions> providerOptions,
    ILogger logger
)
{
    public IReadOnlyList<SyncRootInfo> GetSyncRoots()
    {
        var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
        return roots
            .Where((x) => x.Id.StartsWith(providerOptions.Value.ProviderId + "!"))
            .Select((x) => new SyncRootInfo
            {
                Id = x.Id,
                Name = x.DisplayNameResource,
                Directory = x.Path.Path,
            })
            .ToArray();
    }

    public static bool IsRegistered(string id) => StorageProviderSyncRootManager.GetCurrentSyncRoots().Any((x) => x.Id == id);

    public string GetId(string accountId) => $"{providerOptions.Value.ProviderId}!{WindowsIdentity.GetCurrent().User}!{accountId}";

    public StorageProviderSyncRootInfo? Register(RegisterSyncRootCommand command, IStorageFolder directory, SftpContext context)
    {
        // Stage 1: Setup
        //--------------------------------------------------------------------------------------------
        // Deliberately mark the sync root NotContentIndexed.
        //
        // The previous behaviour cleared this flag on the belief that states need indexing to
        // display. They do not; the shell gets them from the sync root registration itself. What
        // indexing actually did was make Windows Search read every placeholder, and because the
        // hydration policy is Full, each read downloaded the entire file from the phone. On a
        // device with a large photo/video library that is a full mirror of remote storage.
        var clientDirectory = new DirectoryInfo(command.Directory);
        if (!clientDirectory.Attributes.HasFlag(System.IO.FileAttributes.NotContentIndexed))
        {
            clientDirectory.Attributes |= System.IO.FileAttributes.NotContentIndexed;
        }

        var id = GetId(command.AccountId);
        var contextBuffer = CryptographicBuffer.CreateFromByteArray(StructBytes.ToBytes(context));

        var info = new StorageProviderSyncRootInfo
        {
            Id = id,
            Path = directory,
            DisplayNameResource = command.Name,
            IconResource = command.IconResource,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed |
                                     StorageProviderHydrationPolicyModifier.AllowFullRestartHydration,
            PopulationPolicy = (StorageProviderPopulationPolicy)command.PopulationPolicy,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime |
                           StorageProviderInSyncPolicy.DirectoryCreationTime |
                           StorageProviderInSyncPolicy.FileLastWriteTime |
                           StorageProviderInSyncPolicy.DirectoryLastWriteTime |
                           StorageProviderInSyncPolicy.PreserveInsyncForSyncEngine |
                           StorageProviderInSyncPolicy.Default,
            ShowSiblingsAsGroup = false,
            Version = "1.0.0",
            //HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            // RecycleBinUri = new Uri(""),
            Context = contextBuffer,
        };
         //info.StorageProviderItemPropertyDefinitions.Add()

        if (IsRegistered(id))
        {
            return info;
        }
        
        logger.Debug($"Registering {id}");
        StorageProviderSyncRootManager.Register(info);

        return info;
    }

    public void Unregister(string id)
    {
        logger.Debug($"Unregistering {id}");
        try
        {
            // Try to force garbage collection and cleanup before unregistering
            GC.Collect();
            GC.WaitForPendingFinalizers();
            StorageProviderSyncRootManager.Unregister(id);
        }
        catch (COMException ex) when (ex.HResult == -2147023728)
        {
            logger.Error("Sync root not found", ex);
        }
        catch (Exception ex)
        {
            logger.Error("Unregister sync root failed", ex);
        }
    }
}
