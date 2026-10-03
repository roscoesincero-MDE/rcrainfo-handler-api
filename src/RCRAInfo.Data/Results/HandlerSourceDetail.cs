using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
/// The whole of one handler version -- all 215 projected columns -- as returned by
/// <c>dbo.uspGetHandlerSourceDetail</c> (script 505).
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
/// no change tracking. This is the only shape in the project that carries PII: contact names,
/// telephone numbers, email addresses and contact mailing addresses. Nothing read through it may be
/// copied into a log message. Script 505's header puts it plainly -- a CATCH that said which
/// handler's contact record failed to load would be writing a name into a table the monitoring web
/// app can read.
/// </para>
/// <para>
/// The property list mirrors the procedure's projection exactly -- name, order and type. It was
/// emitted from <c>sys.dm_exec_describe_first_result_set</c> rather than typed, and
/// <c>build/check_result_shapes.py</c> re-derives it from the deployed procedure on every guardrail
/// run: the projection and this type drift in one direction and drift silently, because a column
/// this type does not name is simply not materialised and nothing fails.
/// </para>
/// <para>
/// Nullability follows the engine's answer, not intent. Where the projection reports a column
/// nullable it is nullable here, even where the procedure cannot in fact produce a null, because
/// declaring a nullable column non-nullable is the direction that throws at runtime. The reverse is
/// always safe.
/// </para>
/// </remarks>
[Keyless]
public sealed class HandlerSourceDetail
{
    public int HandlerSourceId { get; init; }
    public string HandlerId { get; init; } = null!;
    public string ActivityLocation { get; init; } = null!;
    public string SourceType { get; init; } = null!;
    public int Sequence { get; init; }
    public string? SourceTypeDescription { get; init; }
    public long? SourceTypeSortOrder { get; init; }
    public bool? CurrentRecord { get; init; }
    public bool? ExtractFlag { get; init; }
    public DateOnly? ReceivedDate { get; init; }
    public string? HandlerName { get; init; }
    public string? NonNotifierCode { get; init; }
    public string? NonNotifierDescription { get; init; }
    public string? Acknowledgement { get; init; }
    public string? AccessibilityCode { get; init; }
    public string? AccessibilityDescription { get; init; }
    public string? SiteLocationStreetNumber { get; init; }
    public string? SiteLocationAddress1 { get; init; }
    public string? SiteLocationAddress2 { get; init; }
    public string? SiteLocationCity { get; init; }
    public string? SiteLocationStateActivityLocation { get; init; }
    public string? SiteLocationStateCode { get; init; }
    public string? SiteLocationStateDescription { get; init; }
    public bool? SiteLocationStateActive { get; init; }
    public string? SiteLocationForeignStateActivityLocation { get; init; }
    public string? SiteLocationForeignStateCode { get; init; }
    public string? SiteLocationForeignStateDescription { get; init; }
    public bool? SiteLocationForeignStateActive { get; init; }
    public string? SiteLocationForeignStateName { get; init; }
    public string? SiteLocationForeignStateCountryCode { get; init; }
    public string? SiteLocationCountryActivityLocation { get; init; }
    public string? SiteLocationCountryCode { get; init; }
    public string? SiteLocationCountryDescription { get; init; }
    public bool? SiteLocationCountryActive { get; init; }
    public string? SiteLocationZip { get; init; }
    public double? SiteLocationLatitude { get; init; }
    public double? SiteLocationLongitude { get; init; }
    public bool? SiteLocationLatLongPrimary { get; init; }
    public string? SiteLocationGisOriginActivityLocation { get; init; }
    public string? SiteLocationGisOriginCode { get; init; }
    public string? SiteLocationGisOriginDescription { get; init; }
    public bool? SiteLocationGisOriginActive { get; init; }
    public string? SiteLocationCountyActivityLocation { get; init; }
    public string? SiteLocationCountyCode { get; init; }
    public string? SiteLocationCountyDescription { get; init; }
    public bool? SiteLocationCountyActive { get; init; }
    public string? SiteLocationStateDistrictActivityLocation { get; init; }
    public string? SiteLocationStateDistrictCode { get; init; }
    public string? SiteLocationStateDistrictDescription { get; init; }
    public bool? SiteLocationStateDistrictActive { get; init; }
    public bool? SiteLocationStandardized { get; init; }
    public string? LandTypeCode { get; init; }
    public string? LandTypeDescription { get; init; }
    public string? SiteMailingAddressStreetNumber { get; init; }
    public string? SiteMailingAddressAddress1 { get; init; }
    public string? SiteMailingAddressAddress2 { get; init; }
    public string? SiteMailingAddressCity { get; init; }
    public string? SiteMailingAddressStateActivityLocation { get; init; }
    public string? SiteMailingAddressStateCode { get; init; }
    public string? SiteMailingAddressStateDescription { get; init; }
    public bool? SiteMailingAddressStateActive { get; init; }
    public string? SiteMailingAddressForeignStateActivityLocation { get; init; }
    public string? SiteMailingAddressForeignStateCode { get; init; }
    public string? SiteMailingAddressForeignStateDescription { get; init; }
    public bool? SiteMailingAddressForeignStateActive { get; init; }
    public string? SiteMailingAddressForeignStateName { get; init; }
    public string? SiteMailingAddressForeignStateCountryCode { get; init; }
    public string? SiteMailingAddressCountryActivityLocation { get; init; }
    public string? SiteMailingAddressCountryCode { get; init; }
    public string? SiteMailingAddressCountryDescription { get; init; }
    public bool? SiteMailingAddressCountryActive { get; init; }
    public string? SiteMailingAddressZip { get; init; }
    public string? NaicsPrimaryActivityLocation { get; init; }
    public string? NaicsPrimaryCode { get; init; }
    public string? NaicsPrimaryDescription { get; init; }
    public bool? NaicsPrimaryActive { get; init; }
    public string? ContactFirstName { get; init; }
    public string? ContactMiddleInitial { get; init; }
    public string? ContactLastName { get; init; }
    public string? ContactTitle { get; init; }
    public string? ContactPhone { get; init; }
    public string? ContactPhoneExtension { get; init; }
    public string? ContactFax { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactLanguageActivityLocation { get; init; }
    public string? ContactLanguageCode { get; init; }
    public string? ContactLanguageDescription { get; init; }
    public bool? ContactLanguageActive { get; init; }
    public string? ContactAddressStreetNumber { get; init; }
    public string? ContactAddressAddress1 { get; init; }
    public string? ContactAddressAddress2 { get; init; }
    public string? ContactAddressCity { get; init; }
    public string? ContactAddressStateActivityLocation { get; init; }
    public string? ContactAddressStateCode { get; init; }
    public string? ContactAddressStateDescription { get; init; }
    public bool? ContactAddressStateActive { get; init; }
    public string? ContactAddressForeignStateActivityLocation { get; init; }
    public string? ContactAddressForeignStateCode { get; init; }
    public string? ContactAddressForeignStateDescription { get; init; }
    public bool? ContactAddressForeignStateActive { get; init; }
    public string? ContactAddressForeignStateName { get; init; }
    public string? ContactAddressForeignStateCountryCode { get; init; }
    public string? ContactAddressCountryActivityLocation { get; init; }
    public string? ContactAddressCountryCode { get; init; }
    public string? ContactAddressCountryDescription { get; init; }
    public bool? ContactAddressCountryActive { get; init; }
    public string? ContactAddressZip { get; init; }
    public string? WasteFederalGeneratorCategoryActivityLocation { get; init; }
    public string? WasteFederalGeneratorCategoryCode { get; init; }
    public string? WasteFederalGeneratorCategoryDescription { get; init; }
    public bool? WasteFederalGeneratorCategoryActive { get; init; }
    public string? WasteStateGeneratorCategoryActivityLocation { get; init; }
    public string? WasteStateGeneratorCategoryCode { get; init; }
    public string? WasteStateGeneratorCategoryDescription { get; init; }
    public bool? WasteStateGeneratorCategoryActive { get; init; }
    public bool? WasteFurnaceExemption { get; init; }
    public bool? WasteMixedWasteGenerator { get; init; }
    public bool? WasteOnsiteBurnerExemption { get; init; }
    public bool? WasteReceivesOffSite { get; init; }
    public bool? WasteRecognizedTraderExporter { get; init; }
    public bool? WasteRecognizedTraderImporter { get; init; }
    public bool? WasteRecyclerActivity { get; init; }
    public bool? WasteRecyclerActivityNonStorage { get; init; }
    public bool? WasteShortTermGenerator { get; init; }
    public string? WasteShortTermGeneratorNotes { get; init; }
    public bool? WasteSlabExporter { get; init; }
    public bool? WasteSlabImporter { get; init; }
    public bool? WasteSubPartKCollege { get; init; }
    public bool? WasteSubPartKHospital { get; init; }
    public bool? WasteSubPartKNonprofit { get; init; }
    public bool? WasteSubPartKWithdrawal { get; init; }
    public bool? WasteSubPartPHealthCare { get; init; }
    public bool? WasteSubPartPReverseDistributor { get; init; }
    public bool? WasteSubPartPWithdrawal { get; init; }
    public bool? WasteTransferFacility { get; init; }
    public bool? WasteTransporter { get; init; }
    public bool? WasteTsd { get; init; }
    public bool? WasteUndergroundInjectionControl { get; init; }
    public bool? WasteUniversalWasteDestinationFacility { get; init; }
    public bool? WasteUsImporter { get; init; }
    public bool? WasteUsedOilBurner { get; init; }
    public bool? WasteUsedOilMarketBurner { get; init; }
    public bool? WasteUsedOilProcessor { get; init; }
    public bool? WasteUsedOilRefiner { get; init; }
    public bool? WasteUsedOilSpecMarketer { get; init; }
    public bool? WasteUsedOilTransferFacility { get; init; }
    public bool? WasteUsedOilTransporter { get; init; }
    public string? PermitContactFirstName { get; init; }
    public string? PermitContactMiddleInitial { get; init; }
    public string? PermitContactLastName { get; init; }
    public string? PermitContactTitle { get; init; }
    public string? PermitContactPhone { get; init; }
    public string? PermitContactPhoneExtension { get; init; }
    public string? PermitContactEmail { get; init; }
    public string? PermitContactAddressStreetNumber { get; init; }
    public string? PermitContactAddressAddress1 { get; init; }
    public string? PermitContactAddressAddress2 { get; init; }
    public string? PermitContactAddressCity { get; init; }
    public string? PermitContactAddressStateActivityLocation { get; init; }
    public string? PermitContactAddressStateCode { get; init; }
    public string? PermitContactAddressStateDescription { get; init; }
    public bool? PermitContactAddressStateActive { get; init; }
    public string? PermitContactAddressForeignStateActivityLocation { get; init; }
    public string? PermitContactAddressForeignStateCode { get; init; }
    public string? PermitContactAddressForeignStateDescription { get; init; }
    public bool? PermitContactAddressForeignStateActive { get; init; }
    public string? PermitContactAddressForeignStateName { get; init; }
    public string? PermitContactAddressForeignStateCountryCode { get; init; }
    public string? PermitContactAddressCountryActivityLocation { get; init; }
    public string? PermitContactAddressCountryCode { get; init; }
    public string? PermitContactAddressCountryDescription { get; init; }
    public bool? PermitContactAddressCountryActive { get; init; }
    public string? PermitContactAddressZip { get; init; }
    public DateOnly? PermitFacilityExistenceDate { get; init; }
    public string? PermitNatureOfBusiness { get; init; }
    public bool? HsmManaged { get; init; }
    public bool? HsmFinancialAssurance { get; init; }
    public string? HsmReasonCode { get; init; }
    public string? HsmReasonDescription { get; init; }
    public DateOnly? HsmEffectiveDate { get; init; }
    public bool? LqgSiteClosureCompliance { get; init; }
    public DateOnly? LqgSiteClosureExpectedClosureDate { get; init; }
    public DateOnly? LqgSiteClosureRequestedClosureDate { get; init; }
    public DateOnly? LqgSiteClosureDateClosed { get; init; }
    public string? LqgSiteClosureClosureTypeCode { get; init; }
    public string? LqgSiteClosureClosureTypeDescription { get; init; }
    public string? EpisodicEventTypeActivityLocation { get; init; }
    public string? EpisodicEventTypeCode { get; init; }
    public string? EpisodicEventTypeDescription { get; init; }
    public bool? EpisodicEventTypeActive { get; init; }
    public string? EpisodicContactFirstName { get; init; }
    public string? EpisodicContactMiddleInitial { get; init; }
    public string? EpisodicContactLastName { get; init; }
    public string? EpisodicContactPhone { get; init; }
    public string? EpisodicContactPhoneExt { get; init; }
    public string? EpisodicContactEmail { get; init; }
    public DateOnly? EpisodicBeginDate { get; init; }
    public DateOnly? EpisodicEndDate { get; init; }
    public bool? EpisodicRescind { get; init; }
    public string? EpisodicRescindComment { get; init; }
    public string? Comments { get; init; }
    public string? PublicComments { get; init; }
    public DateOnly? SrcUpdatedDate { get; init; }
    public string? SrcUpdatedBy { get; init; }
    public DateOnly? SrcCreatedDate { get; init; }
    public string? SrcCreatedBy { get; init; }
    public bool? LastRecord { get; init; }
    public bool? ElectronicManifestBroker { get; init; }
    public bool? BrExempt { get; init; }
    public bool? IncludeInNationalReport { get; init; }
    public int? ReportCycle { get; init; }
    public string AuditCreatedBy { get; init; } = null!;
    public DateTimeOffset AuditCreatedDateUtc { get; init; }
    public string AuditModifiedBy { get; init; } = null!;
    public DateTimeOffset AuditModifiedDateUtc { get; init; }
}
