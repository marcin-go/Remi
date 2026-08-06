namespace Remi.Domain;

/// <summary>
/// Invoice intake options transcribed from the data-validation lists in the supplied MI workbooks:
/// RM1557-13 (June 2026), RM1557-14 (June 2026), and RM6259 (June 2026).
/// </summary>
public sealed record MiInvoiceIntakeForm(
    FrameworkCode Framework,
    bool IsAvailable,
    IReadOnlyList<string> Lots,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ServiceGroupsByLot,
    IReadOnlyList<string> ServiceGroupLevel2Options,
    IReadOnlyList<string> UnitOfMeasureOptions)
{
    public bool IsVas => Framework == FrameworkCode.VerticalApplicationSolutions;

    public IReadOnlyList<string> ServiceGroupsFor(string? lotNumber) =>
        !string.IsNullOrWhiteSpace(lotNumber) && ServiceGroupsByLot.TryGetValue(lotNumber.Trim(), out var groups)
            ? groups
            : [];
}

public static class MiInvoiceIntakeForms
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> GCloudGroups =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["1"] =
            [
                "Archiving, Backup and Disaster Recovery", "Block Storage", "Compute and Application Hosting", "Container Service",
                "Content Delivery Network", "Data Warehousing", "Database", "Distributed Denial of Service Attack (DDOS) Protection",
                "Firewall", "Infrastructure and Platform Security", "Intrusion Detection", "Load Balancing", "Logging and Analysis",
                "Message Queuing and Processing", "Networking (including Network as a Service)", "NoSQL database", "Object Storage",
                "Platform as a Service (PaaS)", "Protective Monitoring", "Relational Database", "Search", "Storage",
            ],
            ["2"] =
            [
                "Accounting and Finance", "Analytics and Business Intelligence", "Application Security", "Collaborative Working",
                "Creative, Design and Publishing", "Customer Relationship Management (CRM)",
                "Electronic Document and Records Management (EDRM)", "Healthcare", "Human Resources and Employee Management",
                "Information and Communication Technology (ICT)", "Legal and Enforcement", "Marketing", "Operations Management",
                "Project management and Planning", "Sales", "Schools, Education and Libraries", "Software Development Tools",
                "Transport and Logistics",
            ],
            ["3"] = ["Ongoing Support", "Planning", "Security Services", "Setup and Migration", "Testing", "Training"],
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> VasGroups =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["1"] = ["Civil Enforcement", "Grant Management and Grant Administration", "Payment Processing and Cash Receipting", "Revenues and Benefits", "Other"],
            ["2"] =
            [
                "Learning Application", "Academic Scheduling and Management", "Education Management Information Systems including Special Education",
                "Academic Payment Solutions", "Student Transport Systems", "Communication and Student Experience", "Community Health",
                "Social Care Case Management", "Social Care Other", "Community Health or Social Care Finance", "Enterprise Health e-Rostering",
                "Enterprise Health Electronic Job Planning", "Enterprise Health Electronic Appraisal and Revalidation", "Enterprise Health Acuity and dependency",
                "Enterprise Health Mobile Applications - Temporary staff booking applications", "Enterprise Health ERP", "Enterprise Health HR and Payroll",
                "Safeguarding Software", "Other",
            ],
            ["3"] =
            [
                "Animal welfare", "Building Control", "Built Environment", "Case administration from initial report",
                "Environmental Accounting systems (including CNZ)", "Flood defence", "Food safety", "Geographic Information System (GIS)",
                "Hazard assessment and prioritization", "Licensing", "Local Land Charges", "Noise and pest control", "Officer task management",
                "Planning", "Property, Housing Management", "Regulatory Services", "Street works", "Waste Management", "Other",
            ],
            ["4"] = ["Burials and Crematoria", "Coroner Case Management", "Democratic and Citizen Engagement", "Library", "Museum", "Registrar", "Sports and Recreation", "Other"],
            ["5"] =
            [
                "Case and Custody Applications", "Command and Control, Integrated Command and Control Systems (ICCS)", "Crime (investigation)",
                "Data Analytics (including Management Information and Business Intelligence)", "Digital Asset Management", "Emergency Response and Crisis Management",
                "Forensics (including digital forensics)", "Fraud Detection", "Intelligence", "Real-time analytics", "Recording and Audio-visual",
                "Surveillance, Reconnaissance – overt and covert", "Other",
            ],
        };

    private static readonly MiInvoiceIntakeForm GCloud13 = new(
        FrameworkCode.GCloud13,
        true,
        ["1", "2", "3"],
        GCloudGroups,
        [],
        ["Per Unit", "Per User"]);

    private static readonly MiInvoiceIntakeForm GCloud14 = GCloud13 with { Framework = FrameworkCode.GCloud14 };

    private static readonly MiInvoiceIntakeForm Vas = new(
        FrameworkCode.VerticalApplicationSolutions,
        true,
        ["1", "2", "3", "4", "5"],
        VasGroups,
        ["Software", "Hardware", "Associated Service"],
        []);

    private static readonly MiInvoiceIntakeForm GCloud15 = new(
        FrameworkCode.GCloud15,
        false,
        [],
        new Dictionary<string, IReadOnlyList<string>>(),
        [],
        []);

    public static MiInvoiceIntakeForm For(FrameworkCode framework) => framework switch
    {
        FrameworkCode.GCloud13 => GCloud13,
        FrameworkCode.GCloud14 => GCloud14,
        FrameworkCode.VerticalApplicationSolutions => Vas,
        FrameworkCode.GCloud15 => GCloud15,
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, null),
    };
}
