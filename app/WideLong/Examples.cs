using System.Globalization;

namespace WideLong;

public sealed record ExampleDefinition(string Title, string Description, string TryThis, Func<Session> Create, bool Stack = false, string IdLabel = "participant_id", string TimeLabel = "time");

public static class Examples
{
    public static IReadOnlyList<ExampleDefinition> All { get; } = [
        new("Health study · people, visits, labs and wide measurements", "600 fictional participants in five files: demographics and enrollment (one row per person), visits and lab results (one row per visit), and health measurements repeated across columns.", "Try switching ID + visit to ID, or sort by study group and age. household_size is stored as text with a few \"unknown\" codes.", Health),
        new("School · grades, attendance and behavior across terms", "120 fictional students in five files. Grades are wide (math_term1, math_term2); attendance, behavior and reading logs are long (one row per student per term); student details are one row per student.", "See every file line up by student and term. minutes_read is stored as text with \"n/a\" codes.", School, IdLabel: "student_id", TimeLabel: "term"),
        new("Shop · customers, orders, returns and support", "80 fictional customers in five files. Each customer row matches several order rows; returns and support tickets match individual orders; loyalty details are one row per customer.", "Sort by region and order total, then keep only the columns you need.", Shop, IdLabel: "customer_id", TimeLabel: "order_number"),
        new("Monthly reports · stack matching files", "Five monthly budget files (January–May) with the same columns. Stack them into one table; each row keeps the name of the file it came from.", "Compare months by department, or change matching rules after switching from stacking.", Reports, Stack: true, IdLabel: "department", TimeLabel: "month"),
        new("Survey · codes and messy text", "60 fictional respondents in five files with leading-zero IDs, NA and \"not given\" codes, yes/no answers, scores repeated across columns, and follow-up calls per wave.", "Replace not given with blank in age, then change age to Number; export the cleaning code to rerun it.", Survey, IdLabel: "respondent_id", TimeLabel: "wave"),
        new("Household · roles and repeated visits", "4 fictional households in five files. Mother, Father and Child share a household ID, so ID + person + time identifies each record. Household and contact details are one row per ID; person details are one row per ID + person; questionnaires are long; scores are wide.", "Look at H001 / Child: only Baseline appears. Right-click person → Filter responses… to keep only some roles.", Household, IdLabel: "household_id", TimeLabel: "time")
    ];

    private static void Ready(Source s) { s.IdChosen = true; s.GroupChosen = true; s.EntityChosen = true; s.CaptureOriginal(); }
    private static Session Finish(params Source[] sources) { foreach (var s in sources) Ready(s); return new() { Sources = [.. sources] }; }
    private static Source Table(string name, string[] names, int id, int time = -1, int entity = -1) => new() { Name = name, IdColumn = id, TimeColumn = time, EntityColumn = entity, Fields = names.Select(n => new Field { Name = n, Original = n }).ToList(), IdReason = "This fictional example already identifies its matching ID / grouping columns." };
    private static void Number(Source s, params int[] columns) { foreach (int c in columns) s.Fields[c].Kind = ValueKind.Number; }

    private static Session Health()
    {
        var s = Demo.Create();
        var labs = Table("Lab_results_long.csv", ["participant_id", "visit", "cholesterol_mg_dl", "hba1c_pct", "fasting"], 0, 1); Number(labs, 2, 3);
        var enrollment = Table("Enrollment.csv", ["participant_id", "enrolled_on", "site", "referral_source", "household_size"], 0); enrollment.Fields[1].Kind = ValueKind.Date;
        string[] referral = ["Clinic", "Community event", "Online", "Friend"];
        for (int p = 1; p <= 600; p++)
        {
            string id = p.ToString("D5", CultureInfo.InvariantCulture);
            foreach (string visit in new[] { "Baseline", "Month 6" })
                labs.Rows.Add([id, visit, (165 + p % 60 - (visit == "Month 6" ? 4 : 0)).ToString(CultureInfo.InvariantCulture), (5.2m + p % 14 / 10m).ToString(CultureInfo.InvariantCulture), p % 9 == 0 ? "no" : "yes"]);
            // household_size stays Text with a few codes so opening the example shows the numeric-column prompt.
            enrollment.Rows.Add([id, new DateTime(2024, 10 + p % 3, 1 + p % 28).ToString("yyyy-MM-dd"), "Site " + (char)('A' + p % 3), referral[p % 4], p % 23 == 0 ? "unknown" : (1 + p % 6).ToString(CultureInfo.InvariantCulture)]);
        }
        foreach (var x in new[] { labs, enrollment }) Ready(x);
        s.Sources.Add(labs); s.Sources.Add(enrollment); return s;
    }
    private static Session School()
    {
        var grades = Table("Student_grades_wide.csv", ["student_id", "student_name", "year_group", "math_term1", "math_term2", "reading_term1", "reading_term2"], 0);
        for (int c = 3; c < 7; c++) { grades.Fields[c].Measure = c < 5 ? "math_score" : "reading_score"; grades.Fields[c].Occasion = c % 2 == 1 ? "Term 1" : "Term 2"; } Number(grades, 2, 3, 4, 5, 6);
        var attendance = Table("Attendance_long.csv", ["student_id", "term", "days_present", "days_absent"], 0, 1); Number(attendance, 2, 3);
        var students = Table("Students.csv", ["student_id", "homeroom", "free_meals", "bus_route"], 0);
        var behavior = Table("Behavior_long.csv", ["student_id", "term", "merits", "incidents"], 0, 1); Number(behavior, 2, 3);
        var reading = Table("Reading_log_long.csv", ["student_id", "term", "books_read", "minutes_read"], 0, 1); Number(reading, 2);
        for (int p = 1; p <= 120; p++)
        {
            string id = p.ToString("D4"); grades.Rows.Add([id, "Student " + p, (6 + p % 3).ToString(), (55 + p % 40).ToString(), (58 + p % 40).ToString(), (60 + p % 35).ToString(), (63 + p % 35).ToString()]);
            students.Rows.Add([id, "Room " + (101 + p % 6), p % 4 == 0 ? "yes" : "no", p % 5 == 0 ? "Walker" : "Route " + (1 + p % 4)]);
            for (int t = 1; t <= 2; t++)
            {
                attendance.Rows.Add([id, "Term " + t, (60 - p % 6).ToString(), (p % 6).ToString()]);
                behavior.Rows.Add([id, "Term " + t, (p * t % 9).ToString(), (p % 13 == 0 ? 2 : p % 7 == 0 ? 1 : 0).ToString()]);
                reading.Rows.Add([id, "Term " + t, (2 + (p + t) % 7).ToString(), p % 17 == t ? "n/a" : (300 + p % 50 * 10 + t * 25).ToString()]);
            }
        }
        return Finish(grades, attendance, students, behavior, reading);
    }
    private static Session Shop()
    {
        var customers = Table("Customers.csv", ["customer_id", "customer_name", "region", "membership"], 0);
        var orders = Table("Orders.csv", ["customer_id", "order_number", "order_id", "order_date", "order_total", "delivery_status"], 0, 1); Number(orders, 4); orders.Fields[3].Kind = ValueKind.Date;
        var returns = Table("Order_returns.csv", ["customer_id", "order_number", "return_reason", "refund_amount"], 0, 1); Number(returns, 3);
        var tickets = Table("Support_tickets.csv", ["customer_id", "order_number", "tickets_opened", "satisfaction"], 0, 1); Number(tickets, 2, 3);
        var loyalty = Table("Loyalty.csv", ["customer_id", "points_balance", "member_since", "newsletter"], 0); Number(loyalty, 1); loyalty.Fields[2].Kind = ValueKind.Date;
        string[] reasons = ["Damaged", "Wrong size", "Changed mind"];
        for (int p = 1; p <= 80; p++)
        {
            string id = "C" + p.ToString("D3"); customers.Rows.Add([id, "Customer " + p, new[] { "North", "South", "East", "West" }[p % 4], p % 3 == 0 ? "Plus" : "Standard"]);
            loyalty.Rows.Add([id, (p * 37 % 900).ToString(CultureInfo.InvariantCulture), new DateTime(2020 + p % 5, 1 + p % 12, 1 + p % 27).ToString("yyyy-MM-dd"), p % 2 == 0 ? "yes" : "no"]);
            for (int t = 1; t <= 3; t++)
            {
                decimal total = 19.95m + p * 2 + t / 2m;
                orders.Rows.Add([id, "Order " + t, $"ORD-{p:D3}-{t}", new DateTime(2025, t, 1 + p % 25).ToString("yyyy-MM-dd"), total.ToString(CultureInfo.InvariantCulture), p % 9 == 0 && t == 3 ? "Pending" : "Delivered"]);
                if ((p + t) % 11 == 0) returns.Rows.Add([id, "Order " + t, reasons[(p + t) % 3], (total / 2).ToString(CultureInfo.InvariantCulture)]);
                if ((p * t) % 7 == 0) tickets.Rows.Add([id, "Order " + t, (1 + p % 3).ToString(), (1 + (p + t) % 5).ToString()]);
            }
        }
        return Finish(customers, orders, returns, tickets, loyalty);
    }
    private static Session Reports()
    {
        var sources = new List<Source>();
        foreach (string month in new[] { "January", "February", "March", "April", "May" })
        {
            var s = Table(month + "_budget.csv", ["department", "month", "budget", "spending", "manager"], 0, 1); Number(s, 2, 3);
            string[] departments = ["Operations", "Research", "Support", "Sales", "Finance", "Training"];
            for (int d = 0; d < departments.Length; d++) s.Rows.Add([departments[d], month, (10000 + d * 2000).ToString(), (8500 + d * 1700 + sources.Count * 450).ToString(), "Manager " + (d + 1)]);
            sources.Add(s);
        }
        return Finish(sources.ToArray());
    }
    private static Session Survey()
    {
        var s = Table("Survey_responses.csv", ["respondent_id", "age", "consent", "region", "satisfaction_wave1", "satisfaction_wave2"], 0);
        s.Fields[4].Measure = s.Fields[5].Measure = "satisfaction"; s.Fields[4].Occasion = "Wave 1"; s.Fields[5].Occasion = "Wave 2";
        var respondents = Table("Respondents.csv", ["respondent_id", "age_group", "household_size", "language"], 0);
        var calls = Table("Follow_up_calls.csv", ["respondent_id", "wave", "call_outcome", "call_minutes"], 0, 1);
        var incentives = Table("Incentives.csv", ["respondent_id", "gift_card", "gift_card_value"], 0);
        var panel = Table("Panel_status.csv", ["respondent_id", "wave", "still_enrolled", "contact_attempts"], 0, 1); Number(panel, 3);
        string[] outcomes = ["Reached", "Voicemail", "No answer"];
        for (int p = 1; p <= 60; p++)
        {
            string id = p.ToString("D5");
            s.Rows.Add([id, p % 8 == 0 ? "not given" : (20 + p % 45).ToString(), p % 7 == 0 ? "no" : "yes", p % 2 == 0 ? "Rural" : "Urban", p % 6 == 0 ? "NA" : (1 + p % 5).ToString(), p % 11 == 0 ? "NA" : (1 + (p + 1) % 5).ToString()]);
            respondents.Rows.Add([id, p % 8 == 0 ? "NA" : (20 + p % 45) < 40 ? "18-39" : "40+", p % 10 == 0 ? "NA" : (1 + p % 5).ToString(), p % 5 == 0 ? "Spanish" : "English"]);
            incentives.Rows.Add([id, p % 4 == 0 ? "no" : "yes", p % 4 == 0 ? "0" : (10 + p % 3 * 5).ToString()]);
            for (int w = 1; w <= 2; w++)
            {
                if ((p + w) % 3 != 0) calls.Rows.Add([id, "Wave " + w, outcomes[(p + w) % 3], (p + w) % 3 == 1 ? (3 + p % 9).ToString() : "NA"]);
                panel.Rows.Add([id, "Wave " + w, w == 2 && p % 13 == 0 ? "no" : "yes", (1 + (p + w) % 4).ToString()]);
            }
        }
        return Finish(s, respondents, calls, incentives, panel);
    }
    private static Session Household()
    {
        var session = CompoundFixture();
        var people = Table("Person_details.csv", ["household_id", "person", "age", "employment"], 0, entity: 1); Number(people, 2);
        people.EntityReason = "person separates Mother, Father and Child. Details apply at every time for that person.";
        var contacts = Table("Household_contacts.csv", ["household_id", "preferred_language", "contact_by_text", "members"], 0); Number(contacts, 3);
        string[] roles = ["Mother", "Father", "Child"];
        for (int h = 1; h <= 4; h++)
        {
            string id = "H" + h.ToString("D3");
            for (int r = 0; r < 3; r++) people.Rows.Add([id, roles[r], (r == 2 ? 6 + h * 2 : 30 + h * 3 + r * 2).ToString(CultureInfo.InvariantCulture), r == 2 ? "Student" : (h + r) % 3 == 0 ? "Part time" : "Full time"]);
            contacts.Rows.Add([id, h == 3 ? "Spanish" : "English", h % 2 == 0 ? "yes" : "no", "3"]);
        }
        foreach (var x in new[] { people, contacts }) Ready(x);
        session.Sources.Add(people); session.Sources.Add(contacts); return session;
    }

    public static Session CompoundFixture()
    {
        var households = Table("Household_details.csv", ["household_id", "household_name", "area"], 0);
        households.IdReason = "One row per fictional household; household_id links the three files.";
        households.EntityReason = "No person column: household details apply to every person in that household.";
        var questionnaire = Table("Person_questionnaire_long.csv", ["household_id", "person", "time", "feedback_score", "record_status"], 0, time: 2, entity: 1);
        questionnaire.IdReason = "household_id identifies a household. Several people can share that ID.";
        questionnaire.EntityReason = "person separates Mother, Father and Child within each household.";
        questionnaire.GroupReason = "time identifies the visit within an ID + person pair. The complete ID + person + time key is unique.";
        Number(questionnaire, 3);
        var scores = Table("Person_scores_wide.csv", ["household_id", "person", "confidence_baseline", "confidence_month3", "confidence_month6", "focus_baseline", "focus_month3", "focus_month6"], 0, entity: 1);
        scores.IdReason = "One wide row per household_id + person; repeated score columns identify visits.";
        scores.EntityReason = "person separates Mother and Father. Children have questionnaire rows only in this example.";
        scores.GroupReason = "Visits come from repeated-column labels: Baseline, Month 3 and Month 6.";
        string[] times = ["Baseline", "Month 3", "Month 6"], people = ["Mother", "Father", "Child"];
        for (int c = 2; c < scores.Fields.Count; c++)
        {
            scores.Fields[c].Measure = c < 5 ? "confidence" : "focus";
            scores.Fields[c].Occasion = times[(c - 2) % 3];
            scores.Fields[c].Kind = ValueKind.Number;
        }
        // The long source intentionally has uneven follow-up schedules. Wide rows
        // exist only for Mother/Father, so they must not invent Child visits.
        int[][][] schedule = [
            [[0, 1, 2], [0, 1, 2], [0]],
            [[0, 1, 2], [0, 1], [0, 2]],
            [[0, 2], [0], [0, 1, 2]],
            [[0, 1, 2], [0, 1], [0]]
        ];
        for (int h = 1; h <= 4; h++)
        {
            string id = "H" + h.ToString("D3");
            households.Rows.Add([id, "Household " + h, new[] { "North", "South", "East", "West" }[h - 1]]);
            for (int person = 0; person < people.Length; person++)
                foreach (int t in schedule[h - 1][person])
                    questionnaire.Rows.Add([id, people[person], times[t], h == 3 && person == 2 && t == 1 ? "" : (10 * h + person * 3 + t).ToString(CultureInfo.InvariantCulture), "Complete"]);
            for (int person = 0; person < 2; person++)
            {
                var row = new List<string> { id, people[person] };
                for (int measure = 0; measure < 2; measure++)
                    for (int t = 0; t < times.Length; t++)
                        row.Add((h == 2 && person == 1 && measure == 0 && t == 1) || (h == 4 && person == 0 && measure == 1 && t == 2) ? "" : (measure == 0 ? 50 + h * 5 + person * 2 + t : 60 + h * 3 + person * 2 + t).ToString(CultureInfo.InvariantCulture));
                scores.Rows.Add(row.ToArray());
            }
        }
        return Finish(households, questionnaire, scores);
    }
}
