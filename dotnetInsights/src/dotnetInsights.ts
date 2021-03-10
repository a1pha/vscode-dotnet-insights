import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

export class DepNodeProvider implements vscode.TreeDataProvider<Dependency> {
    private _onDidChangeTreeData: vscode.EventEmitter<Dependency | undefined | void> = new vscode.EventEmitter<Dependency | undefined | void>();
    readonly onDidChangeTreeData: vscode.Event<Dependency | undefined | void> = this._onDidChangeTreeData.event;

    constructor(private workspaceRoot: string) {
    }

    getTreeItem(element: Dependency): vscode.TreeItem {
        return element;
    }

    getChildren(element?: Dependency): Thenable<Dependency[]> {
        if (!this.workspaceRoot) {
            vscode.window.showInformationMessage('No dependency in empty workspace');
            return Promise.resolve([]);
        }

        return Promise.resolve([]);
    }

}

export class Dependency extends vscode.TreeItem {

    constructor(
        public readonly label: string,
        private readonly version: string,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public readonly command?: vscode.Command
    ) {
        super(label, collapsibleState);

        this.tooltip = `${this.label}-${this.version}`;
        this.description = this.version;
    }

    iconPath = {
        light: path.join(__filename, '..', '..', 'resources', 'light', 'dependency.svg'),
        dark: path.join(__filename, '..', '..', 'resources', 'dark', 'dependency.svg')
    };

    contextValue = 'dependency';
}

export class DotnetInsights {
    public ilDasmPath: string;
    public ilDasmVersion: string;

    public pmiPath: string;
    public coreRoot: string;
    public coreRunPath: string;

    public sdkVersions: string[];

    public ilDasmOutputPath: string;
    public pmiOutputPath: string;

    public useIldasm: boolean;
    public usePmi: boolean

    constructor() {
        this.ilDasmPath = "";
        this.ilDasmVersion = "";

        this.pmiPath = "";
        this.coreRoot = "";
        this.coreRunPath = "";

        this.ilDasmOutputPath = "";
        this.pmiOutputPath = "";

        this.useIldasm = false;
        this.usePmi = true;

        this.sdkVersions = [] as string[];
    }

    public setUseIldasm() {
        this.useIldasm = true;
        this.usePmi = false;
    }

    public setUsePmi() {
        this.useIldasm = false;
        this.usePmi = true;
    }
}