using Desk.Core.Domain;

namespace Desk.Infrastructure.Bootstrap;

internal enum StepKind
{
    Assign,
    Open,
    Reply,
    Note,
    Ask,
    Answer,
    Resolve,
    Close,
    SetPriority,
    SetCategory,
    Escalate,
}

/// <summary>
/// One thing that happened to a demo ticket, <paramref name="Minute"/> minutes after its creation. The seeder replays the steps with
/// the domain rules (<c>Ticket.ChangeStatus</c>, <c>ChangePriority</c>, <c>TicketPolicy</c>), exactly like the API would.
/// <paramref name="Staff"/> is an index of <see cref="DemoTickets.Staff"/>.
/// </summary>
internal sealed record Step(StepKind Kind, int Minute, int Staff = -1, string? Text = null, Priority? Priority = null, string? Category = null);

/// <summary>A fictional ticket: when it was created, who asked, and its history.</summary>
internal sealed record DemoTicket(
    int AgeMinutes, string Title, string Description, Priority Priority, string? Category, int Requester, params Step[] Steps);

internal static class DemoTickets
{
    public const string FictionalNote = "\n\n(Ticket de démonstration, données fictives.)";

    public const int Alex = 0;
    public const int Camille = 1;
    public const int Samir = 2;

    /// <summary>The staff the demo tickets are assigned to: the demo administrator and agent, plus one extra agent.</summary>
    public static readonly (string Email, string Name, Role Role)[] Staff =
    [
        DemoAccounts.ByDemoRole["admin"],
        DemoAccounts.ByDemoRole["agent"],
        ("samir.agent@demo.deskflow.example", "Samir Agent (démo)", Role.Agent),
    ];

    /// <summary>The requesters of the demo tickets: the demo requester (index 0) plus three fictional colleagues.</summary>
    public static readonly (string Email, string Name, Role Role)[] Requesters =
    [
        DemoAccounts.ByDemoRole["demandeur"],
        ("lea.martin@demo.deskflow.example", "Léa Martin (démo)", Role.Requester),
        ("karim.benali@demo.deskflow.example", "Karim Benali (démo)", Role.Requester),
        ("ines.roux@demo.deskflow.example", "Inès Roux (démo)", Role.Requester),
    ];

    private const string Accounts = "Accès et comptes";
    private const string Hardware = "Matériel";
    private const string Mail = "Messagerie";
    private const string Network = "Réseau et VPN";
    private const string Software = "Logiciels";

    private static Step Assign(int minute, int staff) => new(StepKind.Assign, minute, staff);

    private static Step Open(int minute, int staff) => new(StepKind.Open, minute, staff);

    private static Step Reply(int minute, int staff, string text) => new(StepKind.Reply, minute, staff, text);

    private static Step Note(int minute, int staff, string text) => new(StepKind.Note, minute, staff, text);

    private static Step Ask(int minute, int staff, string text) => new(StepKind.Ask, minute, staff, text);

    private static Step Answer(int minute, string text) => new(StepKind.Answer, minute, Text: text);

    private static Step Resolve(int minute, int staff, string text) => new(StepKind.Resolve, minute, staff, text);

    private static Step Close(int minute, int staff) => new(StepKind.Close, minute, staff);

    private static Step Raise(int minute, int staff, Priority to) => new(StepKind.SetPriority, minute, staff, Priority: to);

    private static Step Categorize(int minute, int staff, string category) => new(StepKind.SetCategory, minute, staff, Category: category);

    /// <summary>Automatic escalation (EscalationConsumer): happens just after the first missed deadline, no author.</summary>
    private static Step Escalate() => new(StepKind.Escalate, 0);

    private const int Day = 24 * 60;

    /// <summary>
    /// 30 tickets: 5 New, 8 Open, 5 Pending, 6 Resolved, 6 Closed; the four priorities; created over 30 days (the oldest 29 days ago).
    /// Rule: every New or Open ticket is within its deadlines unless it was escalated (a marker and EscalatedAt exist), so the
    /// SlaWatcher finds nothing to emit at start. Two tickets are about to breach (a High one in about an hour, an Urgent one in
    /// minutes): the watcher will really escalate them during a live demo.
    /// </summary>
    public static readonly IReadOnlyList<DemoTicket> All =
    [
        // Closed (6)
        new(29 * Day, "Création de compte pour un nouvel arrivant", "Une nouvelle collègue du service achats commence lundi : il lui faut un compte, une adresse e-mail et l'accès à l'intranet.",
            Priority.Normal, Accounts, 1,
            Assign(10, Camille), Open(12, Camille), Reply(40, Camille, "Bonjour, je prépare le compte. Pouvez-vous me confirmer le service et le responsable ?"),
            Answer(95, "Service achats, responsable : M. Girard. Merci !"),
            Resolve(300, Camille, "Le compte et la messagerie sont créés. Le mot de passe provisoire a été remis à son responsable."), Close(2 * Day, Camille)),
        new(26 * Day, "Installer Visual Studio Code sur mon poste", "J'ai besoin de Visual Studio Code pour modifier des fichiers de configuration. L'installation est bloquée par un droit administrateur.",
            Priority.Low, Software, 2,
            Assign(120, Samir), Open(125, Samir), Reply(400, Samir, "Le logiciel figure dans la liste autorisée, je le déploie sur votre poste."),
            Resolve(2 * Day, Samir, "Visual Studio Code est installé. Il apparaît dans le menu Démarrer après un redémarrage."), Close(4 * Day, Samir)),
        new(23 * Day, "VPN impossible depuis le domicile", "Depuis ce matin le client VPN affiche « Échec de la connexion » alors que mon Internet fonctionne.",
            Priority.High, Network, 0,
            Assign(5, Camille), Open(6, Camille), Reply(20, Camille, "Bonjour, pouvez-vous essayer en partage de connexion depuis votre téléphone ?"),
            Answer(50, "Avec le partage de connexion ça marche, donc c'est ma box."),
            Note(70, Camille, "Box de l'opérateur : le port du VPN est filtré. Rien à corriger côté entreprise."),
            Resolve(200, Camille, "La box bloquait le protocole VPN. Après redémarrage de la box et mise à jour de son micrologiciel, la connexion est stable."), Close(3 * Day, Camille)),
        new(19 * Day, "Clavier qui écrit en QWERTY", "Depuis la mise à jour de la nuit, mon clavier écrit en QWERTY : les lettres A et Q sont inversées.",
            Priority.Normal, Hardware, 3,
            Assign(30, Samir), Open(31, Samir), Reply(90, Samir, "La mise à jour a changé la disposition du clavier. Pouvez-vous ajouter « Français (AZERTY) » dans les paramètres de langue ?"),
            Resolve(600, Samir, "La disposition AZERTY est de nouveau la disposition par défaut (réglage appliqué à distance)."), Close(3 * Day, Samir)),
        new(15 * Day, "Boîte e-mail inaccessible avant un rendez-vous client", "Je n'arrive plus à ouvrir ma messagerie et j'ai un rendez-vous client dans une heure : j'ai besoin de mes pièces jointes.",
            Priority.Urgent, Mail, 1,
            Assign(3, Camille), Open(4, Camille), Reply(10, Camille, "Je regarde tout de suite. Pouvez-vous essayer le webmail en attendant ?"),
            Note(35, Camille, "Quota dépassé (50 Go) à cause d'une archive volumineuse. Je libère de la place."),
            Resolve(120, Camille, "Le quota est rétabli, la messagerie fonctionne de nouveau. Pensez à archiver les anciens messages."), Close(2 * Day, Camille)),
        new(12 * Day, "Licence Adobe Reader expirée", "Le lecteur PDF indique que ma licence a expiré et refuse d'ouvrir les documents protégés.",
            Priority.Low, Software, 2,
            Assign(200, Samir), Open(210, Samir), Reply(700, Samir, "Merci, je renouvelle la licence."),
            Resolve(3 * Day, Samir, "La licence est renouvelée pour un an."), Close(5 * Day, Samir)),

        // Resolved (6)
        new(11 * Day, "Mot de passe expiré, impossible de me connecter", "Au démarrage du poste, Windows indique que mon mot de passe a expiré et ne me laisse pas en choisir un nouveau.",
            Priority.Normal, Accounts, 3,
            Assign(15, Camille), Open(16, Camille), Reply(30, Camille, "Bonjour, je réinitialise votre mot de passe. Vous recevrez un code sur votre application d'authentification."),
            Resolve(90, Camille, "Le mot de passe est réinitialisé : pensez à le mettre à jour aussi sur votre téléphone pour éviter un nouveau verrouillage.")),
        new(8 * Day + 8 * 60, "Écran noir sur la station d'accueil", "Depuis lundi, mes deux écrans restent noirs quand je pose le portable sur la station d'accueil.",
            Priority.High, Hardware, 0,
            Assign(8, Samir), Open(10, Samir), Reply(40, Samir, "Pouvez-vous débrancher la station du secteur pendant 30 secondes, puis la rebrancher ?"),
            Answer(75, "Je viens d'essayer, le premier écran s'affiche mais pas le second."),
            Resolve(300, Samir, "Le câble DisplayPort du second écran était défectueux. Il a été remplacé.")),
        new(6 * Day + 6 * 60, "Accès refusé au partage Comptabilité", "Le dossier partagé « Comptabilité » affiche « Accès refusé » depuis mon retour de congés.",
            Priority.Normal, Network, 1,
            Assign(20, Camille), Open(22, Camille), Reply(50, Camille, "Votre accès a probablement expiré pendant vos congés. Je vérifie vos groupes."),
            Raise(70, Camille, Priority.High),
            Note(80, Camille, "Le compte a été retiré du groupe Comptabilité par la revue trimestrielle des droits. Validation demandée à la responsable."),
            Resolve(240, Camille, "La responsable a validé l'accès, le groupe est rétabli. Fermez et rouvrez la session pour en profiter.")),
        new(4 * Day + 4 * 60, "Excel se bloque à l'ouverture d'un fichier volumineux", "Excel se fige pendant plusieurs minutes quand j'ouvre le classeur de suivi budgétaire (80 Mo).",
            Priority.Normal, Software, 3,
            Assign(30, Samir), Open(31, Samir), Reply(200, Samir, "J'ai reproduit le blocage. Je regarde la version et les compléments installés."),
            Escalate(),
            Resolve(2200, Samir, "Un complément obsolète ralentissait le chargement. Il est désactivé et le classeur s'ouvre en quelques secondes.")),
        new(2 * Day + 12 * 60, "Mettre à jour la signature e-mail", "Merci de remplacer ma signature par le nouveau modèle avec le logo à jour.",
            Priority.Low, Mail, 0,
            Assign(200, Camille), Open(210, Camille), Reply(900, Camille, "Le nouveau modèle est prêt, je le publie."),
            Resolve(1500, Camille, "La signature est mise à jour dans Outlook et dans le webmail.")),
        new(30 * 60, "Wi-Fi instable dans la salle de réunion B", "En visioconférence dans la salle B, la connexion se coupe toutes les dix minutes.",
            Priority.High, Network, 2,
            Assign(15, Camille), Open(16, Camille), Reply(45, Camille, "Merci, je regarde les journaux de la borne de la salle B."),
            Note(120, Camille, "La borne tourne sur le canal saturé par le bâtiment voisin. Changement de canal programmé."),
            Resolve(400, Camille, "Le canal Wi-Fi de la salle B a été changé : plus de coupure sur nos tests d'une heure.")),

        // Pending (5)
        new(5 * Day, "Souris sans fil qui se déconnecte", "Ma souris sans fil se déconnecte plusieurs fois par heure et le curseur reste figé pendant quelques secondes.",
            Priority.Normal, Hardware, 1,
            Assign(30, Samir), Open(31, Samir), Reply(120, Samir, "Quelle référence de souris avez-vous ? Est-elle branchée via un récepteur USB ?"),
            Answer(400, "C'est une souris du service avec un petit récepteur USB."),
            Ask(1000, Samir, "Pouvez-vous essayer avec des piles neuves et un autre port USB, puis me dire si cela s'améliore ?")),
        new(70 * 60, "Double authentification : nouveau téléphone", "J'ai changé de téléphone et je ne peux plus valider la double authentification pour me connecter.",
            Priority.High, Accounts, 2,
            Assign(10, Camille), Open(12, Camille), Reply(25, Camille, "Bonjour, pour des raisons de sécurité je dois d'abord vérifier votre identité."),
            Ask(40, Camille, "Pouvez-vous m'appeler au poste 1204, ou envoyer le nom de votre responsable pour que je lui demande une validation ?")),
        new(48 * 60, "Demande d'installation de Power BI", "Mon équipe aimerait utiliser Power BI Desktop pour préparer les tableaux de bord mensuels.",
            Priority.Low, Software, 3,
            Assign(300, Camille), Open(310, Camille), Reply(600, Camille, "Le logiciel nécessite une licence nominative et l'accord de votre responsable."),
            Ask(700, Camille, "Pouvez-vous faire valider la demande par votre responsable (réponse à ce ticket suffit) ?")),
        new(20 * 60, "Calendrier partagé non synchronisé sur mobile", "Les rendez-vous du calendrier de l'équipe n'apparaissent plus sur mon téléphone depuis hier.",
            Priority.Normal, Mail, 0,
            Assign(30, Samir), Open(33, Samir), Reply(90, Samir, "Je vérifie le calendrier partagé côté serveur."),
            Note(130, Samir, "Côté serveur tout est correct : le compte du téléphone doit être reconfiguré."),
            Ask(160, Samir, "Pouvez-vous supprimer puis rajouter le compte professionnel sur le téléphone (voir l'article « Synchroniser la messagerie et le calendrier sur un mobile »), puis me confirmer ?")),
        new(8 * 60, "Imprimante du 2e étage hors ligne", "L'imprimante du couloir du 2e étage est « Hors ligne » depuis ce matin, personne ne peut imprimer.",
            Priority.High, Hardware, 1,
            Assign(10, Camille), Open(11, Camille), Reply(25, Camille, "Merci. Je vérifie à distance si l'imprimante répond sur le réseau."),
            Ask(60, Camille, "Elle ne répond pas à distance. Quelqu'un peut-il regarder sur place si elle est allumée et si le câble réseau est branché ?")),

        // Open (8)
        new(60 * 60, "Mettre à jour Firefox vers la dernière version", "Mon navigateur Firefox est en version 115 et le site de la banque refuse de s'afficher correctement.",
            Priority.Low, Software, 2,
            Assign(60, Camille), Open(65, Camille), Reply(500, Camille, "La mise à jour fait partie du prochain déploiement. Je regarde si on peut l'avancer pour votre poste.")),
        new(18 * 60, "Partage réseau très lent le matin", "Entre 8 h et 10 h, l'ouverture des fichiers du partage Projets prend plusieurs minutes.",
            Priority.Normal, Network, 3,
            Assign(30, Samir), Open(32, Samir), Reply(90, Samir, "Je regarde l'utilisation du serveur de fichiers aux heures indiquées."),
            Note(240, Samir, "Pic d'accès à 9 h lié à la sauvegarde des postes. Voir avec l'équipe infrastructure pour décaler la sauvegarde.")),
        new(10 * 60, "Demande d'accès au dossier RH", "Je reprends le suivi des congés et j'ai besoin d'un accès en lecture au dossier partagé RH.",
            Priority.Normal, null, 0,
            Assign(20, Camille), Open(22, Camille), Categorize(25, Camille, Accounts), Reply(60, Camille, "J'ai transmis la demande à la responsable RH pour validation.")),
        new(5 * 60, "Outlook ne reçoit plus les messages", "Je ne reçois plus aucun nouveau message depuis ce matin, mais le webmail fonctionne.",
            Priority.High, Mail, 2,
            Assign(10, Camille), Open(11, Camille), Reply(30, Camille, "Le webmail fonctionne, donc la boîte est saine : le problème vient du profil Outlook. Je prépare un nouveau profil.")),
        new(7 * 60, "Ordinateur portable qui surchauffe", "Mon portable chauffe fortement et le ventilateur tourne en permanence, même avec un seul logiciel ouvert.",
            Priority.High, Hardware, 1,
            Assign(20, Samir), Open(25, Samir), Reply(60, Samir, "Je lance un diagnostic matériel à distance."),
            Note(180, Samir, "Ventilateur encrassé. Un échange du portable est à prévoir si le nettoyage ne suffit pas.")),
        new(150, "VPN indisponible pour toute l'équipe commerciale", "Aucun commercial n'arrive à se connecter au VPN depuis une heure ; nous avons une démonstration client cet après-midi.",
            Priority.High, Network, 3,
            Assign(4, Camille), Open(5, Camille), Reply(8, Camille, "Je suis dessus. Pouvez-vous me confirmer combien de personnes sont touchées ?"),
            Raise(15, Camille, Priority.Urgent),
            Note(25, Camille, "Le concentrateur VPN refuse les nouvelles sessions : licence de sessions simultanées atteinte. Contact de l'équipe infrastructure."),
            Reply(40, Camille, "Cause identifiée côté infrastructure, la correction est en cours.")),
        new(6 * 60, "Serveur de fichiers inaccessible", "Plus aucun dossier du serveur de fichiers ne s'ouvre, tout le site est concerné.",
            Priority.Urgent, Network, 2,
            Assign(5, Camille), Open(6, Camille), Reply(12, Camille, "Incident confirmé, je préviens l'équipe infrastructure."),
            Note(30, Camille, "Disque plein sur le serveur. En attente de l'extension de capacité."),
            Escalate()),
        new(60, "Message d'activation de la licence Office", "Un bandeau « Produit non activé » apparaît en haut de Word et Excel.",
            Priority.Normal, Software, 0,
            Assign(5, Samir), Open(8, Samir), Reply(20, Samir, "Je réactive la licence depuis la console, cela prend quelques minutes.")),

        // New (5)
        new(30 * 60, "Compte verrouillé après trois essais", "Mon compte est verrouillé depuis hier soir, je ne peux plus ouvrir de session.",
            Priority.Normal, Accounts, 2,
            Escalate()),
        new(3 * 60, "Besoin d'un second écran", "Pour le suivi des tickets, un second écran m'aiderait : je jongle entre trois fenêtres toute la journée.",
            Priority.Low, Hardware, 1),
        new(90, "Installer le logiciel de visioconférence", "Je dois animer des réunions externes la semaine prochaine, il me faut l'application de visioconférence installée.",
            Priority.Normal, Software, 3),
        new(30, "Accès Wi-Fi invité pour un visiteur", "Un prestataire vient demain matin pour une intervention et aura besoin d'un accès Wi-Fi invité.",
            Priority.High, Network, 0),
        new(12, "Messages d'hameçonnage reçus par plusieurs collègues", "Plusieurs collègues viennent de recevoir un e-mail qui imite la direction et demande de payer une facture urgente.",
            Priority.Urgent, Mail, 2),
    ];
}
