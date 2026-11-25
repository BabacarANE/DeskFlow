namespace Desk.Infrastructure.Bootstrap;

/// <summary>One fictional knowledge base article of the demo. <paramref name="PublishedDaysAgo"/> is null for a draft.</summary>
internal sealed record DemoArticle(
    string Title, string Symptom, string Cause, string Solution, string[] Tags, int CreatedDaysAgo, int? PublishedDaysAgo);

/// <summary>The 12 fictional articles of the demo company (9 published, 3 drafts). Every one carries the tag "données fictives".</summary>
internal static class DemoArticles
{
    public const string FictionalTag = "données fictives";

    public static readonly IReadOnlyList<DemoArticle> All =
    [
        new("VPN : connexion impossible ou coupée",
            "Le client VPN affiche « Échec de la connexion » ou se déconnecte au bout de quelques minutes. Les applications internes (intranet, partages) ne répondent plus.",
            "Le plus souvent, le mot de passe a expiré, le client VPN n'est plus à jour, ou la box du domicile bloque le protocole VPN. Une coupure Wi-Fi brève suffit aussi à rompre le tunnel.",
            "1. Vérifiez que vous accédez à Internet (ouvrez un site public).\n2. Quittez complètement le client VPN, puis relancez-le.\n3. Reconnectez-vous avec votre identifiant d'entreprise ; si le mot de passe est refusé, suivez l'article « Mot de passe expiré ou compte verrouillé ».\n4. Depuis un domicile, essayez un partage de connexion de votre téléphone : si cela fonctionne, la box bloque le VPN (redémarrez-la).\n5. Si l'échec persiste, ouvrez un ticket en indiquant l'heure, le message exact et votre lieu de connexion.",
            ["vpn", "réseau", "télétravail", FictionalTag], 28, 27),
        new("L'imprimante n'imprime plus (file d'impression bloquée)",
            "Le document part dans la file d'impression mais rien ne sort. L'état de l'imprimante affiche « Hors ligne » ou « En attente » sur le poste.",
            "Un travail corrompu bloque la file d'attente du spouleur, ou l'imprimante n'est plus joignable sur le réseau (câble débranché, adresse IP modifiée).",
            "1. Vérifiez que l'imprimante est allumée et affiche « Prête » à l'écran.\n2. Sur le poste, ouvrez « Imprimantes et scanners », sélectionnez l'imprimante puis « Ouvrir la file d'attente ».\n3. Annulez tous les documents en attente.\n4. Si la file refuse de se vider, redémarrez le service « Spouleur d'impression » (ou redémarrez le poste).\n5. Relancez une impression de test.\n6. Si l'imprimante reste « Hors ligne », notez l'étiquette collée dessus et ouvrez un ticket.",
            ["imprimante", "matériel", "impression", FictionalTag], 26, 25),
        new("Mot de passe expiré ou compte verrouillé",
            "À l'ouverture de session, Windows ou le portail indique « Votre mot de passe a expiré » ou « Ce compte est verrouillé ». Les applications d'entreprise refusent la connexion.",
            "Le mot de passe est renouvelé tous les 90 jours. Le compte se verrouille aussi après cinq essais erronés, par exemple à cause d'un téléphone qui garde l'ancien mot de passe.",
            "1. Sur le portail de libre-service, choisissez « Mot de passe oublié ».\n2. Validez votre identité avec le code reçu sur votre application d'authentification.\n3. Choisissez un nouveau mot de passe d'au moins 12 caractères, différent des trois derniers.\n4. Mettez à jour le mot de passe sur votre téléphone et votre messagerie mobile, sinon le compte se verrouillera de nouveau.\n5. Un compte verrouillé se débloque seul après 30 minutes ; sinon ouvrez un ticket pour un déblocage immédiat.",
            ["compte", "mot de passe", "accès", FictionalTag], 25, 24),
        new("Outlook ne reçoit plus les messages",
            "La boîte de réception n'affiche plus de nouveaux messages depuis plusieurs heures, la barre d'état indique « Déconnecté » ou « Mise à jour de la boîte de réception en cours ».",
            "Outlook est passé en mode hors connexion, le profil local est corrompu ou la boîte aux lettres a atteint son quota.",
            "1. Dans l'onglet « Envoi / réception », vérifiez que « Travailler hors connexion » n'est pas activé.\n2. Vérifiez le quota : « Fichier > Outils de nettoyage > Taille de la boîte aux lettres » ; videz les éléments supprimés au besoin.\n3. Fermez Outlook, puis rouvrez-le en maintenant la touche Ctrl pour le démarrer en mode sans échec.\n4. Contrôlez l'accès au webmail : si les messages s'y affichent, le problème vient du profil local.\n5. Créez un nouveau profil (Panneau de configuration > Courrier) ou ouvrez un ticket.",
            ["messagerie", "outlook", "e-mail", FictionalTag], 24, 22),
        new("Accéder à un partage réseau (lecteur Z:)",
            "Le message « Accès refusé » ou « Le chemin réseau est introuvable » apparaît en ouvrant un dossier partagé, ou le lecteur Z: a disparu de l'explorateur.",
            "Le poste n'est pas connecté au réseau de l'entreprise (VPN coupé), le lecteur n'est plus mappé, ou le compte n'est pas membre du groupe qui autorise l'accès au dossier.",
            "1. Vérifiez que vous êtes sur le réseau de l'entreprise ou connecté au VPN.\n2. Dans l'explorateur, cliquez sur « Ce PC > Connecter un lecteur réseau » et saisissez le chemin communiqué par votre responsable.\n3. Si « Accès refusé » s'affiche alors que le chemin est correct, demandez à votre responsable de valider l'accès, puis ouvrez un ticket avec le nom exact du dossier.\n4. Après l'ajout au groupe, fermez et rouvrez votre session pour que le droit soit pris en compte.",
            ["partage", "réseau", "accès", FictionalTag], 22, 21),
        new("Wi-Fi lent ou qui se coupe régulièrement",
            "La connexion Wi-Fi est très lente, les visioconférences saccadent ou le poste perd le réseau en se déplaçant dans les locaux.",
            "Signal trop faible loin d'une borne, saturation d'une borne en salle de réunion, ou pilote de la carte Wi-Fi obsolète.",
            "1. Rapprochez-vous d'une borne et vérifiez le nombre de barres de signal.\n2. Désactivez puis réactivez le Wi-Fi (mode avion 10 secondes).\n3. Choisissez le réseau « Entreprise » plutôt que le réseau « Invités ».\n4. Redémarrez le poste pour appliquer les mises à jour du pilote.\n5. Si le problème touche plusieurs personnes dans la même pièce, ouvrez un ticket en indiquant la salle et l'heure.",
            ["wi-fi", "réseau", "connexion", FictionalTag], 20, 19),
        new("Écran noir sur la station d'accueil (dock)",
            "Le portable est posé sur la station d'accueil mais le ou les écrans externes restent noirs ou affichent « Pas de signal ».",
            "Câble vidéo mal enclenché, mauvaise source sélectionnée sur l'écran, ou pilote graphique et micrologiciel de la station à mettre à jour.",
            "1. Vérifiez que l'écran est allumé et sur la bonne entrée (HDMI ou DisplayPort).\n2. Débranchez la station du secteur 30 secondes, rebranchez-la, puis reconnectez le portable.\n3. Appuyez sur Windows + P et choisissez « Étendre » ou « Dupliquer ».\n4. Essayez un autre câble vidéo si vous en avez un.\n5. Si l'écran reste noir, notez le modèle de la station et ouvrez un ticket.",
            ["écran", "dock", "matériel", FictionalTag], 18, 17),
        new("Demander l'installation d'un logiciel",
            "Un logiciel nécessaire au travail n'est pas installé sur le poste et son installation est bloquée par un message d'autorisation administrateur.",
            "Les droits d'installation sont réservés au service informatique. Chaque logiciel doit être validé (licence, sécurité) avant d'être déployé.",
            "1. Consultez le catalogue des logiciels déjà autorisés : l'installation y est automatique depuis le « Centre logiciel ».\n2. Sinon, ouvrez un ticket dans la catégorie « Logiciels » avec le nom du logiciel, la version, l'usage et l'accord de votre responsable.\n3. Le service informatique vérifie la licence et la sécurité, puis déploie le logiciel à distance, en général sous deux jours ouvrés.\n4. N'installez jamais un programme téléchargé par vos propres moyens.",
            ["logiciel", "installation", "licence", FictionalTag], 16, 15),
        new("Activer la double authentification (MFA)",
            "L'application d'entreprise demande un code à six chiffres à la connexion, ou le compte est bloqué après un changement de téléphone.",
            "La double authentification est obligatoire. Elle n'est plus liée au nouveau téléphone si l'application d'authentification n'a pas été transférée.",
            "1. Installez l'application d'authentification recommandée sur le nouveau téléphone.\n2. Connectez-vous au portail depuis un poste, choisissez « Sécurité > Ajouter un appareil » et scannez le code QR.\n3. Validez avec le code à six chiffres affiché, puis supprimez l'ancien appareil.\n4. Conservez les codes de secours dans un coffre de mots de passe.\n5. Si l'ancien téléphone est perdu, ouvrez un ticket : une vérification d'identité sera demandée.",
            ["sécurité", "mfa", "compte", FictionalTag], 14, 12),
        new("Webcam ou micro non détectés en visioconférence",
            "Pendant une visioconférence, l'application affiche « Aucune caméra détectée » ou l'interlocuteur n'entend pas le micro.",
            "À vérifier : l'autorisation d'accès à la caméra et au micro est peut-être désactivée dans les paramètres de confidentialité, ou un autre logiciel utilise déjà la caméra.",
            "1. Fermez les autres applications qui peuvent utiliser la caméra.\n2. Dans « Paramètres > Confidentialité », autorisez l'accès à la caméra et au micro pour l'application.\n3. Dans l'application, choisissez le bon périphérique dans les réglages audio et vidéo.\n4. Débranchez puis rebranchez la webcam USB sur un autre port.",
            ["visioconférence", "matériel", FictionalTag], 5, null),
        new("Espace disque insuffisant sur le poste",
            "Windows affiche « Espace disque insuffisant », les mises à jour échouent ou le poste ralentit fortement.",
            "Le dossier Téléchargements, la corbeille ou les fichiers temporaires occupent la majeure partie du disque. Les mises à jour de Windows nécessitent au moins 10 Go libres.",
            "1. Ouvrez « Paramètres > Système > Stockage » et repérez les catégories les plus lourdes.\n2. Videz la corbeille et le dossier Téléchargements.\n3. Lancez « Nettoyage de disque » pour supprimer les fichiers temporaires.\n4. Déplacez les gros fichiers de travail vers le partage réseau ou le stockage en ligne de l'entreprise.\n5. Ne supprimez jamais de dossiers système : en cas de doute, ouvrez un ticket.",
            ["disque", "stockage", "poste", FictionalTag], 3, null),
        new("Synchroniser la messagerie et le calendrier sur un mobile",
            "Les e-mails ou les rendez-vous de l'agenda n'apparaissent plus sur le téléphone, ou la synchronisation demande sans cesse le mot de passe.",
            "Le mot de passe a changé et n'a pas été mis à jour sur le téléphone, ou le compte a été ajouté avec un protocole non pris en charge.",
            "1. Supprimez le compte professionnel dans les réglages du téléphone.\n2. Ajoutez-le de nouveau avec le type « Exchange » et votre adresse d'entreprise.\n3. Validez la double authentification.\n4. Cochez la synchronisation du courrier, des contacts et du calendrier.\n5. Attendez quelques minutes la première synchronisation.",
            ["messagerie", "mobile", "calendrier", FictionalTag], 1, null),
    ];
}
