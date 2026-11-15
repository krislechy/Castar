#import <UIKit/UIKit.h>

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdint.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

static const char controlSocketPath[] = "/var/jb/var/run/castar/control.sock";
static const char adminSocketPath[] = "/var/jb/var/lib/castar/admin.sock";
static const NSUInteger maximumLogBytes = 16384;
static const NSUInteger maximumAdminBytes = 256 * 1024;

static BOOL WaitForSocket(int fd, short events, NSTimeInterval deadline)
{
    struct pollfd descriptor = { .fd = fd, .events = events };
    for (;;) {
        const NSTimeInterval remaining = deadline - NSProcessInfo.processInfo.systemUptime;
        if (remaining <= 0)
            return NO;
        const int result = poll(&descriptor, 1, (int)(remaining * 1000) + 1);
        if (result >= 0)
            return result > 0 && (descriptor.revents & (events | POLLHUP | POLLERR)) != 0;
        if (errno != EINTR)
            return NO;
    }
}

static NSData *ExchangeCommand(int fd, const char *socketPath, uid_t expectedUser, NSData *command,
                               NSUInteger limit, NSTimeInterval timeout)
{
    const NSTimeInterval deadline = NSProcessInfo.processInfo.systemUptime + timeout;
    const int suppressSignal = 1;
    if (fcntl(fd, F_SETFL, O_NONBLOCK) < 0 ||
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &suppressSignal, sizeof(suppressSignal)) < 0)
        return nil;

    struct sockaddr_un address = { .sun_len = sizeof(address), .sun_family = AF_UNIX };
    size_t pathLength = strlen(socketPath);
    if (pathLength >= sizeof(address.sun_path))
        return nil;
    memcpy(address.sun_path, socketPath, pathLength + 1);
    if (connect(fd, (const struct sockaddr *)&address, sizeof(address)) < 0) {
        if (errno != EINPROGRESS || !WaitForSocket(fd, POLLOUT, deadline))
            return nil;
        int socketError = 0;
        socklen_t length = sizeof(socketError);
        if (getsockopt(fd, SOL_SOCKET, SO_ERROR, &socketError, &length) < 0 || socketError != 0)
            return nil;
    }

    uid_t peerUser;
    gid_t peerGroup;
    if (getpeereid(fd, &peerUser, &peerGroup) < 0 || peerUser != expectedUser)
        return nil;

    const char *request = command.bytes;
    NSUInteger written = 0;
    while (written < command.length && WaitForSocket(fd, POLLOUT, deadline)) {
        const ssize_t count = send(fd, request + written, command.length - written, 0);
        if (count > 0)
            written += (NSUInteger)count;
        else if (count == 0 || (errno != EINTR && errno != EAGAIN && errno != EWOULDBLOCK))
            return nil;
    }
    if (written != command.length)
        return nil;
    shutdown(fd, SHUT_WR);

    NSMutableData *response = [NSMutableData data];
    while (WaitForSocket(fd, POLLIN, deadline)) {
        char buffer[2048];
        const ssize_t count = recv(fd, buffer, sizeof(buffer), 0);
        if (count == 0)
            return response;
        if (count < 0) {
            if (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK)
                continue;
            return nil;
        }
        if ((NSUInteger)count > limit - response.length)
            return nil;
        [response appendBytes:buffer length:(NSUInteger)count];
    }
    return nil;
}

static NSString *DecodeLog(NSData *response)
{
    NSMutableData *sanitized = [NSMutableData dataWithCapacity:response.length];
    const uint8_t *bytes = response.bytes;
    for (NSUInteger index = 0; index < response.length;) {
        const uint8_t first = bytes[index];
        NSUInteger width = 0;
        if (first < 0x80)
            width = 1;
        else if (first >= 0xc2 && first <= 0xdf)
            width = 2;
        else if (first >= 0xe0 && first <= 0xef)
            width = 3;
        else if (first >= 0xf0 && first <= 0xf4)
            width = 4;

        if (width <= response.length - index) {
            for (NSUInteger next = 1; next < width; next++) {
                const uint8_t byte = bytes[index + next];
                if ((byte & 0xc0) != 0x80 ||
                    (next == 1 && ((first == 0xe0 && byte < 0xa0) || (first == 0xed && byte >= 0xa0) ||
                                   (first == 0xf0 && byte < 0x90) || (first == 0xf4 && byte >= 0x90)))) {
                    width = 0;
                    break;
                }
            }
        } else {
            width = 0;
        }
        if (width > 0) {
            [sanitized appendBytes:bytes + index length:width];
            index += width;
        } else {
            const uint8_t replacement[] = { 0xef, 0xbf, 0xbd };
            [sanitized appendBytes:replacement length:sizeof(replacement)];
            index++;
        }
    }
    return [[NSString alloc] initWithData:sanitized encoding:NSUTF8StringEncoding];
}

static NSData *ReadSocketResponse(const char *socketPath, uid_t expectedUser, NSString *verb,
                                  NSUInteger limit, NSTimeInterval timeout)
{
    const int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return nil;
    NSData *command = [[verb stringByAppendingString:@"\n"] dataUsingEncoding:NSUTF8StringEncoding];
    NSData *response = command ? ExchangeCommand(fd, socketPath, expectedUser, command, limit, timeout) : nil;
    close(fd);
    return response;
}

static NSString *SendCommand(NSString *verb, NSUInteger limit, NSTimeInterval timeout)
{
    NSData *response = ReadSocketResponse(controlSocketPath, 0, verb, limit, timeout);
    if (!response)
        return nil;
    NSString *decoded = [[NSString alloc] initWithData:response encoding:NSUTF8StringEncoding];
    return decoded ?: ([verb isEqualToString:@"logs"] ? DecodeLog(response) : nil);
}

static NSString *ReadStatus(void)
{
    NSString *response = SendCommand(@"status", 32, 2);
    return [response stringByTrimmingCharactersInSet:NSCharacterSet.newlineCharacterSet];
}

static NSDictionary *SendAdminCommand(NSString *verb)
{
    NSData *data = ReadSocketResponse(adminSocketPath, geteuid(), verb, maximumAdminBytes, 8);
    if (!data)
        return nil;
    id response = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
    return [response isKindOfClass:NSDictionary.class] ? response : nil;
}

static NSString *ResponseString(NSDictionary *response, NSString *field)
{
    id value = response[field];
    return [value isKindOfClass:NSString.class] ? value : nil;
}

static void CopyApiKey(NSString *value)
{
    [UIPasteboard.generalPasteboard setItems:@[@{@"public.utf8-plain-text": value}]
        options:@{UIPasteboardOptionLocalOnly: @YES,
                  UIPasteboardOptionExpirationDate: [NSDate dateWithTimeIntervalSinceNow:120]}];
}

typedef NS_ENUM(NSInteger, CastarKeysSection) {
    CastarKeysErrorSection,
    CastarKeysAddressesSection,
    CastarKeysListSection,
    CastarKeysSectionCount
};

@interface CastarKeysViewController : UITableViewController
@property(nonatomic, strong) dispatch_queue_t adminQueue;
@property(nonatomic, strong) UIBarButtonItem *createButton;
@property(nonatomic, strong) UIBarButtonItem *refreshButton;
@property(nonatomic, copy) NSArray<NSDictionary *> *keys;
@property(nonatomic, copy) NSString *clientAddress;
@property(nonatomic, copy) NSString *errorMessage;
@property(nonatomic, copy) NSString *pendingKey;
@property(nonatomic, weak) UIAlertController *keyAlert;
@property(nonatomic) BOOL busy;
@property(nonatomic) BOOL visible;
@property(nonatomic) BOOL keyRevealScheduled;
@end

@implementation CastarKeysViewController

- (void)viewDidLoad
{
    [super viewDidLoad];
    self.title = @"Сервер и ключи API";
    self.adminQueue = dispatch_queue_create("org.castar.control.admin", DISPATCH_QUEUE_SERIAL);
    self.createButton = [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemAdd
        target:self action:@selector(createKey:)];
    self.createButton.accessibilityLabel = @"Выпустить ключ API";
    self.refreshButton = [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemRefresh
        target:self action:@selector(refreshAdminInfo:)];
    self.refreshButton.accessibilityLabel = @"Обновить сервер и ключи";
    self.navigationItem.rightBarButtonItems = @[self.createButton, self.refreshButton];
    [self updateAdminButtons];

    NSNotificationCenter *notifications = NSNotificationCenter.defaultCenter;
    [notifications addObserver:self selector:@selector(adminBecameActive:) name:UIApplicationDidBecomeActiveNotification object:nil];
    [notifications addObserver:self selector:@selector(adminResigningActive:) name:UIApplicationWillResignActiveNotification object:nil];
}

- (void)viewDidAppear:(BOOL)animated
{
    [super viewDidAppear:animated];
    self.visible = YES;
    [self showPendingKey];
    [self refreshAdminInfo:nil];
}

- (void)viewWillDisappear:(BOOL)animated
{
    [super viewWillDisappear:animated];
    self.visible = NO;
}

- (void)adminBecameActive:(NSNotification *)notification
{
    (void)notification;
    if (self.visible) {
        [self showPendingKey];
        [self refreshAdminInfo:nil];
    }
}

- (void)adminResigningActive:(NSNotification *)notification
{
    (void)notification;
    self.keyAlert.message = nil;
    [self.keyAlert dismissViewControllerAnimated:NO completion:nil];
}

- (void)updateAdminButtons
{
    self.createButton.enabled = !self.busy && self.keys != nil && self.pendingKey.length == 0;
    self.refreshButton.enabled = !self.busy;
}

- (void)setAdminError:(NSString *)message
{
    self.errorMessage = message;
    [self.tableView reloadData];
}

- (void)performAdminCommand:(NSString *)command result:(void (^)(NSDictionary *))result
{
    if (self.busy)
        return;
    self.busy = YES;
    [self updateAdminButtons];
    dispatch_async(self.adminQueue, ^{
        NSDictionary *response = SendAdminCommand(command);
        dispatch_async(dispatch_get_main_queue(), ^{
            self.busy = NO;
            [self updateAdminButtons];
            NSString *error = ResponseString(response, @"error");
            if (!response || error.length > 0) {
                [self setAdminError:error ?: @"Управление сервером недоступно."];
            } else {
                [self setAdminError:nil];
                result(response);
            }
        });
    });
}

- (void)refreshAdminInfo:(UIBarButtonItem *)sender
{
    (void)sender;
    [self performAdminCommand:@"list" result:^(NSDictionary *response) {
        NSString *client = ResponseString(response, @"clientAddress");
        id entries = response[@"keys"];
        if (!client || ![entries isKindOfClass:NSArray.class]) {
            [self setAdminError:@"Сервер вернул неверный ответ."];
            return;
        }
        for (id entry in entries) {
            NSString *identifier = [entry isKindOfClass:NSDictionary.class] ? ResponseString(entry, @"id") : nil;
            if (!identifier || ![[NSUUID alloc] initWithUUIDString:identifier] ||
                !ResponseString(entry, @"label") || !ResponseString(entry, @"suffix") ||
                ![entry[@"canCopy"] isKindOfClass:NSNumber.class]) {
                [self setAdminError:@"Сервер вернул неверный список ключей."];
                return;
            }
        }
        self.clientAddress = client;
        self.keys = entries;
        [self updateAdminButtons];
        [self.tableView reloadData];
    }];
}

- (NSInteger)numberOfSectionsInTableView:(UITableView *)tableView
{
    (void)tableView;
    return CastarKeysSectionCount;
}

- (NSInteger)tableView:(UITableView *)tableView numberOfRowsInSection:(NSInteger)section
{
    (void)tableView;
    switch (section) {
        case CastarKeysErrorSection: return self.errorMessage.length > 0 ? 1 : 0;
        case CastarKeysAddressesSection: return self.clientAddress ? 1 : 0;
        case CastarKeysListSection: return self.keys ? MAX((NSInteger)1, (NSInteger)self.keys.count) : 0;
        default: return 0;
    }
}

- (NSString *)tableView:(UITableView *)tableView titleForHeaderInSection:(NSInteger)section
{
    (void)tableView;
    return section == CastarKeysListSection && self.keys ? @"Действующие ключи" : nil;
}

- (UITableViewCell *)tableView:(UITableView *)tableView cellForRowAtIndexPath:(NSIndexPath *)indexPath
{
    static NSString *reuseIdentifier = @"CastarAdminRow";
    UITableViewCell *cell = [tableView dequeueReusableCellWithIdentifier:reuseIdentifier];
    if (!cell)
        cell = [[UITableViewCell alloc] initWithStyle:UITableViewCellStyleDefault reuseIdentifier:reuseIdentifier];
    UIListContentConfiguration *text = [UIListContentConfiguration subtitleCellConfiguration];
    cell.selectionStyle = UITableViewCellSelectionStyleNone;
    cell.accessoryType = UITableViewCellAccessoryNone;
    if (indexPath.section == CastarKeysErrorSection) {
        text.text = self.errorMessage;
        text.textProperties.color = UIColor.systemRedColor;
    } else if (indexPath.section == CastarKeysAddressesSection) {
        text.text = @"Адрес сервера";
        NSString *address = self.clientAddress;
        text.secondaryText = address.length > 0 ? address : @"Не настроен";
        text.secondaryTextProperties.numberOfLines = 0;
        if (address.length > 0)
            cell.selectionStyle = UITableViewCellSelectionStyleDefault;
    } else if (self.keys.count > 0) {
        NSDictionary *key = self.keys[indexPath.row];
        NSString *label = ResponseString(key, @"label");
        text.text = label.length > 0 ? label : @"Ключ API";
        text.secondaryText = [NSString stringWithFormat:@"•••• %@", key[@"suffix"]];
        cell.selectionStyle = UITableViewCellSelectionStyleDefault;
        cell.accessoryType = UITableViewCellAccessoryDisclosureIndicator;
    } else {
        text.text = @"Нет действующих ключей";
        text.textProperties.color = UIColor.secondaryLabelColor;
    }
    cell.contentConfiguration = text;
    return cell;
}

- (void)tableView:(UITableView *)tableView didSelectRowAtIndexPath:(NSIndexPath *)indexPath
{
    [tableView deselectRowAtIndexPath:indexPath animated:YES];
    if (self.busy)
        return;
    if (indexPath.section == CastarKeysAddressesSection) {
        NSString *address = self.clientAddress;
        if (address.length > 0)
            [self showCopyAlertWithTitle:@"Адрес сервера" value:address sensitive:NO];
    } else if (indexPath.section == CastarKeysListSection && (NSUInteger)indexPath.row < self.keys.count) {
        [self showActionsForKey:self.keys[indexPath.row] origin:indexPath];
    }
}

- (void)createKey:(UIBarButtonItem *)sender
{
    (void)sender;
    UIAlertController *prompt = [UIAlertController alertControllerWithTitle:@"Новый ключ API"
        message:nil preferredStyle:UIAlertControllerStyleAlert];
    [prompt addTextFieldWithConfigurationHandler:^(UITextField *field) {
        field.placeholder = @"Имя (необязательно)";
        field.accessibilityLabel = field.placeholder;
    }];
    [prompt addAction:[UIAlertAction actionWithTitle:@"Отмена" style:UIAlertActionStyleCancel handler:nil]];
    __weak UIAlertController *weakPrompt = prompt;
    [prompt addAction:[UIAlertAction actionWithTitle:@"Выпустить" style:UIAlertActionStyleDefault
        handler:^(UIAlertAction *action) {
            (void)action;
            NSString *label = [weakPrompt.textFields.firstObject.text
                stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
            if (label.length > 0 &&
                [label rangeOfCharacterFromSet:NSCharacterSet.controlCharacterSet].location != NSNotFound) {
                [self setAdminError:@"Имя ключа должно быть в одну строку."];
                return;
            }
            NSString *command = label.length > 0 ? [@"create " stringByAppendingString:label] : @"create";
            [self performAdminCommand:command result:^(NSDictionary *response) {
                NSString *key = ResponseString(response, @"key");
                if (key.length == 0) {
                    [self setAdminError:@"Сервер не вернул новый ключ."];
                    return;
                }
                self.pendingKey = key;
                [self updateAdminButtons];
                [self showPendingKey];
                [self refreshAdminInfo:nil];
            }];
        }]];
    [self presentViewController:prompt animated:YES completion:nil];
}

- (void)showPendingKey
{
    if (!self.visible || self.pendingKey.length == 0 ||
        UIApplication.sharedApplication.applicationState != UIApplicationStateActive)
        return;
    if (self.presentedViewController) {
        if (!self.keyRevealScheduled) {
            self.keyRevealScheduled = YES;
            __weak CastarKeysViewController *weakSelf = self;
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC / 5), dispatch_get_main_queue(), ^{
                CastarKeysViewController *controller = weakSelf;
                controller.keyRevealScheduled = NO;
                [controller showPendingKey];
            });
        }
        return;
    }
    NSString *key = self.pendingKey;
    self.pendingKey = nil;
    [self updateAdminButtons];
    [self showCopyAlertWithTitle:@"Новый ключ API" value:key sensitive:YES];
}

- (void)showCopyAlertWithTitle:(NSString *)title value:(NSString *)value sensitive:(BOOL)sensitive
{
    UIAlertController *alert = [UIAlertController alertControllerWithTitle:title message:value
        preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:@"Закрыть" style:UIAlertActionStyleCancel handler:nil]];
    [alert addAction:[UIAlertAction actionWithTitle:@"Скопировать" style:UIAlertActionStyleDefault
        handler:^(UIAlertAction *action) {
            (void)action;
            if (sensitive)
                CopyApiKey(value);
            else
                UIPasteboard.generalPasteboard.string = value;
        }]];
    if (sensitive)
        self.keyAlert = alert;
    [self presentViewController:alert animated:YES completion:nil];
}

- (void)showActionsForKey:(NSDictionary *)key origin:(NSIndexPath *)indexPath
{
    NSString *label = ResponseString(key, @"label");
    NSString *identifier = ResponseString(key, @"id");
    UIAlertController *actions = [UIAlertController alertControllerWithTitle:label.length > 0 ? label : @"Ключ API"
        message:[NSString stringWithFormat:@"•••• %@", key[@"suffix"]] preferredStyle:UIAlertControllerStyleActionSheet];
    if ([key[@"canCopy"] boolValue]) {
        [actions addAction:[UIAlertAction actionWithTitle:@"Скопировать" style:UIAlertActionStyleDefault
            handler:^(UIAlertAction *action) {
                (void)action;
                [self performAdminCommand:[@"copy " stringByAppendingString:identifier] result:^(NSDictionary *response) {
                    NSString *value = ResponseString(response, @"key");
                    if (value.length > 0)
                        CopyApiKey(value);
                    else
                        [self setAdminError:@"Сервер не вернул ключ."];
                }];
            }]];
    }
    [actions addAction:[UIAlertAction actionWithTitle:@"Отозвать" style:UIAlertActionStyleDestructive
        handler:^(UIAlertAction *action) {
            (void)action;
            [self performAdminCommand:[@"revoke " stringByAppendingString:identifier] result:^(NSDictionary *response) {
                id confirmed = response[@"ok"];
                if ([confirmed isKindOfClass:NSNumber.class] && [confirmed boolValue])
                    [self refreshAdminInfo:nil];
                else
                    [self setAdminError:@"Сервер не подтвердил отзыв ключа."];
            }];
        }]];
    [actions addAction:[UIAlertAction actionWithTitle:@"Отмена" style:UIAlertActionStyleCancel handler:nil]];
    actions.popoverPresentationController.sourceView = self.tableView;
    actions.popoverPresentationController.sourceRect = [self.tableView rectForRowAtIndexPath:indexPath];
    [self presentViewController:actions animated:YES completion:nil];
}

@end

@interface CastarStatusViewController : UIViewController
@property(nonatomic, strong) UILabel *statusLabel;
@property(nonatomic, strong) UILabel *actionErrorLabel;
@property(nonatomic, strong) UITextView *logView;
@property(nonatomic, strong) UIButton *restartButton;
@property(nonatomic, strong) UIButton *stopButton;
@property(nonatomic, strong) NSTimer *refreshTimer;
@property(nonatomic, strong) dispatch_queue_t controlQueue;
@property(nonatomic) BOOL visible;
@property(nonatomic) BOOL refreshing;
@property(nonatomic) BOOL controlling;
@property(nonatomic, copy) NSString *serviceStatus;
@property(nonatomic, strong) CastarKeysViewController *keysController;
@end

@implementation CastarStatusViewController

- (void)viewDidLoad
{
    [super viewDidLoad];
    self.title = @"Castar Server";
    self.navigationItem.backButtonDisplayMode = UINavigationItemBackButtonDisplayModeMinimal;
    self.navigationItem.rightBarButtonItem = [[UIBarButtonItem alloc] initWithTitle:@"Ключи API"
        style:UIBarButtonItemStylePlain target:self action:@selector(openKeys:)];
    self.view.backgroundColor = UIColor.systemBackgroundColor;
    self.controlQueue = dispatch_queue_create("org.castar.control.socket", DISPATCH_QUEUE_SERIAL);

    self.statusLabel = [self labelWithStyle:UIFontTextStyleTitle2];
    self.statusLabel.text = @"Проверка статуса…";
    self.restartButton = [self buttonWithTitle:@"Перезапустить" action:@selector(restartService:) filled:YES];
    self.stopButton = [self buttonWithTitle:@"Остановить" action:@selector(stopService:) filled:NO];
    [self updateButtons];

    self.actionErrorLabel = [self labelWithStyle:UIFontTextStyleFootnote];
    self.actionErrorLabel.textColor = UIColor.systemRedColor;
    self.actionErrorLabel.hidden = YES;

    UILabel *logsHeading = [self labelWithStyle:UIFontTextStyleHeadline];
    logsHeading.text = @"Журнал";
    logsHeading.accessibilityTraits |= UIAccessibilityTraitHeader;

    self.logView = [[UITextView alloc] init];
    self.logView.backgroundColor = UIColor.secondarySystemBackgroundColor;
    self.logView.textColor = UIColor.labelColor;
    self.logView.editable = NO;
    self.logView.selectable = YES;
    self.logView.accessibilityLabel = @"Журнал Castar Server";
    self.logView.adjustsFontForContentSizeCategory = YES;
    self.logView.font = [[UIFontMetrics metricsForTextStyle:UIFontTextStyleFootnote]
        scaledFontForFont:[UIFont monospacedSystemFontOfSize:13 weight:UIFontWeightRegular]];

    UIStackView *layout = [[UIStackView alloc] initWithArrangedSubviews:@[
        self.statusLabel, self.restartButton, self.stopButton, self.actionErrorLabel, logsHeading, self.logView
    ]];
    layout.axis = UILayoutConstraintAxisVertical;
    layout.spacing = 10;
    [layout setCustomSpacing:18 afterView:self.statusLabel];
    [layout setCustomSpacing:14 afterView:self.stopButton];
    layout.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:layout];
    UILayoutGuide *margins = self.view.layoutMarginsGuide;
    UILayoutGuide *safeArea = self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [layout.leadingAnchor constraintEqualToAnchor:margins.leadingAnchor],
        [layout.trailingAnchor constraintEqualToAnchor:margins.trailingAnchor],
        [layout.topAnchor constraintEqualToAnchor:safeArea.topAnchor constant:12],
        [layout.bottomAnchor constraintEqualToAnchor:safeArea.bottomAnchor constant:-12]
    ]];

    NSNotificationCenter *notifications = NSNotificationCenter.defaultCenter;
    [notifications addObserver:self selector:@selector(becameActive:) name:UIApplicationDidBecomeActiveNotification object:nil];
    [notifications addObserver:self selector:@selector(resigningActive:) name:UIApplicationWillResignActiveNotification object:nil];
}

- (void)openKeys:(UIBarButtonItem *)sender
{
    (void)sender;
    if (!self.keysController)
        self.keysController = [[CastarKeysViewController alloc] initWithStyle:UITableViewStyleInsetGrouped];
    if (self.navigationController.topViewController == self)
        [self.navigationController pushViewController:self.keysController animated:YES];
}

- (UILabel *)labelWithStyle:(UIFontTextStyle)style
{
    UILabel *label = [[UILabel alloc] init];
    label.font = [UIFont preferredFontForTextStyle:style];
    label.adjustsFontForContentSizeCategory = YES;
    label.numberOfLines = 0;
    [label setContentCompressionResistancePriority:UILayoutPriorityDefaultHigh + 1 forAxis:UILayoutConstraintAxisVertical];
    return label;
}

- (UIButton *)buttonWithTitle:(NSString *)title action:(SEL)action filled:(BOOL)filled
{
    UIButtonConfiguration *configuration = filled
        ? [UIButtonConfiguration filledButtonConfiguration]
        : [UIButtonConfiguration borderedButtonConfiguration];
    configuration.title = title;
    configuration.buttonSize = UIButtonConfigurationSizeLarge;
    if (!filled)
        configuration.baseForegroundColor = UIColor.systemRedColor;
    UIButton *button = [UIButton buttonWithConfiguration:configuration primaryAction:nil];
    [button addTarget:self action:action forControlEvents:UIControlEventTouchUpInside];
    [button.heightAnchor constraintGreaterThanOrEqualToConstant:44].active = YES;
    return button;
}

- (void)viewDidAppear:(BOOL)animated
{
    [super viewDidAppear:animated];
    self.visible = YES;
    [self startRefreshing];
}

- (void)viewWillDisappear:(BOOL)animated
{
    [super viewWillDisappear:animated];
    self.visible = NO;
    [self stopRefreshing];
}

- (void)becameActive:(NSNotification *)notification
{
    (void)notification;
    [self startRefreshing];
}

- (void)resigningActive:(NSNotification *)notification
{
    (void)notification;
    [self stopRefreshing];
}

- (void)startRefreshing
{
    if (self.refreshTimer || !self.visible || UIApplication.sharedApplication.applicationState != UIApplicationStateActive)
        return;
    __weak CastarStatusViewController *weakSelf = self;
    self.refreshTimer = [NSTimer scheduledTimerWithTimeInterval:3 repeats:YES block:^(NSTimer *timer) {
        (void)timer;
        [weakSelf refreshStatus];
    }];
    self.refreshTimer.tolerance = 0.5;
    [self refreshStatus];
}

- (void)stopRefreshing
{
    [self.refreshTimer invalidate];
    self.refreshTimer = nil;
}

- (void)refreshStatus
{
    if (!self.refreshTimer || self.refreshing || self.controlling)
        return;
    self.refreshing = YES;
    dispatch_async(self.controlQueue, ^{
        NSString *status = ReadStatus();
        NSString *log = status ? SendCommand(@"logs", maximumLogBytes, 2) : nil;
        dispatch_async(dispatch_get_main_queue(), ^{
            self.refreshing = NO;
            if (!self.controlling)
                [self updateStatus:status];
            [self updateLog:log];
        });
    });
}

- (void)updateStatus:(NSString *)status
{
    NSDictionary<NSString *, NSString *> *labels = @{
        @"running": @"Работает",
        @"starting": @"Запускается",
        @"stopped": @"Остановлен",
        @"unavailable": @"Недоступен"
    };
    NSString *label = status ? labels[status] : nil;
    self.serviceStatus = label ? status : nil;
    self.statusLabel.text = label ?: @"Недоступен";
    [self updateButtons];
}

- (void)updateButtons
{
    self.restartButton.enabled = self.serviceStatus != nil && !self.controlling;
    self.stopButton.enabled = self.restartButton.enabled && ![self.serviceStatus isEqualToString:@"stopped"];
}

- (void)updateLog:(NSString *)log
{
    NSString *display = log ? (log.length > 0 ? log : @"Записей нет") : @"Журнал недоступен";
    if ([self.logView.text isEqualToString:display])
        return;
    CGPoint previousOffset = self.logView.contentOffset;
    CGFloat previousBottom = MAX(0, self.logView.contentSize.height - self.logView.bounds.size.height);
    const BOOL followTail = previousOffset.y >= previousBottom - 20;
    self.logView.text = display;
    [self.logView layoutIfNeeded];
    if (followTail) {
        [self.logView scrollRangeToVisible:NSMakeRange(display.length, 0)];
    } else {
        const CGFloat contentBottom = MAX(0, self.logView.contentSize.height - self.logView.bounds.size.height);
        previousOffset.y = MIN(previousOffset.y, contentBottom);
        [self.logView setContentOffset:previousOffset animated:NO];
    }
}

- (void)restartService:(UIButton *)sender
{
    (void)sender;
    [self controlService:@"restart" pendingStatus:@"Перезапуск…" failureMessage:@"Не удалось перезапустить службу"];
}

- (void)stopService:(UIButton *)sender
{
    (void)sender;
    [self controlService:@"stop" pendingStatus:@"Остановка…" failureMessage:@"Не удалось остановить службу"];
}

- (void)controlService:(NSString *)command pendingStatus:(NSString *)pendingStatus failureMessage:(NSString *)failureMessage
{
    self.controlling = YES;
    self.actionErrorLabel.hidden = YES;
    self.statusLabel.text = pendingStatus;
    [self updateButtons];
    dispatch_async(self.controlQueue, ^{
        NSString *response = SendCommand(command, 32, 12);
        const BOOL success = [[response stringByTrimmingCharactersInSet:NSCharacterSet.newlineCharacterSet]
            isEqualToString:@"ok"];
        dispatch_async(dispatch_get_main_queue(), ^{
            self.controlling = NO;
            if (!success) {
                self.actionErrorLabel.text = failureMessage;
                self.actionErrorLabel.hidden = NO;
                UIAccessibilityPostNotification(UIAccessibilityAnnouncementNotification, failureMessage);
            }
            [self updateButtons];
            [self refreshStatus];
        });
    });
}

@end

@interface CastarAppDelegate : UIResponder <UIApplicationDelegate>
@property(nonatomic, strong) UIWindow *window;
@end

@implementation CastarAppDelegate

- (BOOL)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)options
{
    (void)application;
    (void)options;
    self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = [[UINavigationController alloc]
        initWithRootViewController:[[CastarStatusViewController alloc] init]];
    [self.window makeKeyAndVisible];
    return YES;
}

@end

int main(int argc, char *argv[])
{
    @autoreleasepool {
        return UIApplicationMain(argc, argv, nil, NSStringFromClass(CastarAppDelegate.class));
    }
}
