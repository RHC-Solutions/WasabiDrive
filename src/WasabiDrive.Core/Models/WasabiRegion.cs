namespace WasabiDrive.Core.Models;

/// <summary>
/// A Wasabi storage region and its S3 service endpoint host.
/// See https://docs.wasabi.com/docs/what-are-the-service-urls-for-wasabis-different-storage-regions
/// </summary>
public sealed record WasabiRegion(string RegionCode, string DisplayName, string Endpoint)
{
    /// <summary>Known Wasabi regions. Endpoints are hostnames (no scheme) as rclone expects.</summary>
    public static readonly IReadOnlyList<WasabiRegion> All = new[]
    {
        new WasabiRegion("us-east-1", "US East 1 (N. Virginia)", "s3.us-east-1.wasabisys.com"),
        new WasabiRegion("us-east-2", "US East 2 (N. Virginia)", "s3.us-east-2.wasabisys.com"),
        new WasabiRegion("us-central-1", "US Central 1 (Texas)", "s3.us-central-1.wasabisys.com"),
        new WasabiRegion("us-west-1", "US West 1 (Oregon)", "s3.us-west-1.wasabisys.com"),
        new WasabiRegion("ca-central-1", "Canada Central 1 (Toronto)", "s3.ca-central-1.wasabisys.com"),
        new WasabiRegion("eu-central-1", "EU Central 1 (Amsterdam)", "s3.eu-central-1.wasabisys.com"),
        new WasabiRegion("eu-central-2", "EU Central 2 (Frankfurt)", "s3.eu-central-2.wasabisys.com"),
        new WasabiRegion("eu-west-1", "EU West 1 (London)", "s3.eu-west-1.wasabisys.com"),
        new WasabiRegion("eu-west-2", "EU West 2 (Paris)", "s3.eu-west-2.wasabisys.com"),
        new WasabiRegion("eu-south-1", "EU South 1 (Milan)", "s3.eu-south-1.wasabisys.com"),
        new WasabiRegion("ap-northeast-1", "AP Northeast 1 (Tokyo)", "s3.ap-northeast-1.wasabisys.com"),
        new WasabiRegion("ap-northeast-2", "AP Northeast 2 (Osaka)", "s3.ap-northeast-2.wasabisys.com"),
        new WasabiRegion("ap-southeast-1", "AP Southeast 1 (Singapore)", "s3.ap-southeast-1.wasabisys.com"),
        new WasabiRegion("ap-southeast-2", "AP Southeast 2 (Sydney)", "s3.ap-southeast-2.wasabisys.com"),
    };

    public static WasabiRegion? FindByCode(string regionCode) =>
        All.FirstOrDefault(r => string.Equals(r.RegionCode, regionCode, StringComparison.OrdinalIgnoreCase));
}
